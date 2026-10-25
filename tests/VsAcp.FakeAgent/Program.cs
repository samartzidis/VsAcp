using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VsAcp.FakeAgent;

// A scriptable ACP agent for tests: newline-delimited JSON-RPC over stdio, driven by a scenario file.
//
// Environment:
//   FAKE_AGENT_SCENARIO  path to the scenario JSON (optional; without it every prompt just answers "OK")
//   FAKE_AGENT_LOG       path of a JSONL file that records everything the agent received and did, so a
//                        test can assert on what the client sent (initialize capabilities, the prompt text
//                        including a post-revert context prefix, permission outcomes, ...). The file is
//                        appended to across agent processes (a revert starts a new one).
//
// Scenario:
//   {
//     "agentCapabilities": { ... },        // initialize result (default: loadSession=false)
//     "newSession": { "configOptions": [], "modes": {...} },   // merged into the session/new result
//     "replayOnLoad": [ <sessionUpdate>, ... ],                // sent during session/load (history replay)
//     "failLoad": true,                                        // session/load answers with an error (session gone)
//     "configOptionsOnSet": [ ... ],                           // result of session/set_config_option (default: [])
//     "turns": [ { "when": "text", "steps": [ <step>, ... ] }, ... ]
//   }
// A prompt runs the first turn whose "when" is contained in the user's text (a turn without "when" is the
// fallback). Matching ignores a revert's <prior_conversation_context> prefix: only the text after the last
// "My next request:" line counts, so a replayed earlier prompt never selects the wrong turn.
//
// Steps (exactly one key each):
//   {"say": "text", "messageId": "m1"}             agent_message_chunk
//   {"think": "text"}                              agent_thought_chunk
//   {"update": { "sessionUpdate": "...", ... }}    any raw session update (sessionId is added by the envelope;
//                                                  "{cwd}" in a string becomes the session's working folder)
//   {"write": {"path": "a.txt", "content": "x"}}   fs/write_text_file through the client (path relative to cwd)
//   {"read": "a.txt"}                              fs/read_text_file through the client (result is logged)
//   {"disk": {"path": "a.txt", "content": "x"}}    write the file directly, bypassing the client (a "terminal" side effect)
//   {"deleteDisk": "a.txt"}                        delete a file directly
//   {"permission": {"title": "Edit a.txt", "options": [{"optionId":"allow","name":"Allow","kind":"allow_once"}]}}
//                                                  session/request_permission; the outcome is logged as event "permissionResult"
//   {"delayMs": 200}
//   {"waitForCancel": true}                        block until session/cancel, then end the turn as "cancelled"
//   {"fail": "message"}                            answer the prompt with a JSON-RPC error
//   {"stopReason": "refusal"}                      end the turn now with this stop reason
//   {"exit": 3}                                    terminate the process (a crash)
//   {"hang": true}                                 stay alive but stop reading and answering (a wedged agent)
internal static class Program
{
    private static int Main()
    {
        var scenarioPath = Environment.GetEnvironmentVariable("FAKE_AGENT_SCENARIO");
        var logPath = Environment.GetEnvironmentVariable("FAKE_AGENT_LOG");

        var scenario = scenarioPath is { Length: > 0 } && File.Exists(scenarioPath)
            ? JsonNode.Parse(File.ReadAllText(scenarioPath)) as JsonObject ?? new JsonObject()
            : new JsonObject();

        var utf8 = new UTF8Encoding(false);
        var input = new StreamReader(Console.OpenStandardInput(), utf8);
        var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };

        return new Agent(scenario, input, output, logPath).RunAsync().GetAwaiter().GetResult();
    }
}

internal sealed class RpcError : Exception
{
    public int Code { get; }
    public RpcError(int code, string message) : base(message) => Code = code;
}

internal sealed class Agent
{
    private const string NextRequestMarker = "My next request:\n";

    private readonly JsonObject _scenario;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly string? _logPath;
    private readonly object _writeLock = new();
    private readonly object _logLock = new();
    private readonly ConcurrentDictionary<string, string> _sessionCwd = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pendingClientRequests = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _cancels = new();
    private volatile bool _hung;
    private int _nextClientRequestId = 1000;
    private int _nextSession;

    public Agent(JsonObject scenario, TextReader input, TextWriter output, string? logPath)
    {
        _scenario = scenario;
        _input = input;
        _output = output;
        _logPath = logPath;
    }

    public async Task<int> RunAsync()
    {
        var running = new List<Task>();
        string? line;
        while ((line = await _input.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            if (line.Length == 0) continue;

            JsonObject? message;
            try { message = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { Log("event", "unparseable", JsonValue.Create(line)); continue; }
            if (message is null) continue;
            if (_hung)
            {
                Log("event", "ignoredWhileHung", message["method"]?.DeepClone());
                continue;
            }

            var method = (string?)message["method"];
            if (method is null)
            {
                // A response to a request this agent made (fs/*, session/request_permission).
                var key = message["id"]?.ToString();
                if (key is not null && _pendingClientRequests.TryRemove(key, out var pending))
                    pending.TrySetResult(message);
                continue;
            }

            Log("in", method, message["params"]);

            if (message["id"] is null)
            {
                HandleNotification(method, message["params"] as JsonObject);
                continue;
            }

            // Requests run off the read loop: a prompt waits for the client's answers to its own requests.
            var request = message;
            running.Add(Task.Run(() => HandleRequestAsync(request, method)));
        }

        await Task.WhenAny(Task.WhenAll(running), Task.Delay(3000)).ConfigureAwait(false);
        return 0;
    }

    private void HandleNotification(string method, JsonObject? parameters)
    {
        if (method != "session/cancel") return;

        var sessionId = (string?)parameters?["sessionId"];
        if (sessionId is not null && _cancels.TryGetValue(sessionId, out var cancel))
            cancel.TrySetResult(true);
    }

    private async Task HandleRequestAsync(JsonObject request, string method)
    {
        var id = request["id"]!.DeepClone();
        var parameters = request["params"] as JsonObject ?? new JsonObject();

        try
        {
            JsonNode result = method switch
            {
                "initialize" => Initialize(),
                "session/new" => NewSession(parameters),
                "session/load" => await LoadSessionAsync(parameters).ConfigureAwait(false),
                "session/prompt" => await PromptAsync(parameters).ConfigureAwait(false),
                "session/set_config_option" => new JsonObject
                {
                    ["configOptions"] = (_scenario["configOptionsOnSet"] as JsonArray)?.DeepClone() ?? new JsonArray()
                },
                "session/set_mode" or "session/delete" or "session/close" => new JsonObject(),
                _ => throw new RpcError(-32601, "Method not found: " + method)
            };

            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
        }
        catch (RpcError ex)
        {
            SendError(id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            SendError(id, -32603, ex.Message);
        }
    }

    private JsonObject Initialize()
    {
        // Lets a test find (and kill) this process, and tell agent processes apart after a restart.
        Log("event", "process", JsonValue.Create(System.Diagnostics.Process.GetCurrentProcess().Id));

        var capabilities = (_scenario["agentCapabilities"] as JsonObject)?.DeepClone()
            ?? new JsonObject { ["loadSession"] = false };

        return new JsonObject
        {
            ["protocolVersion"] = 1,
            ["agentCapabilities"] = capabilities,
            ["authMethods"] = new JsonArray(),
            ["agentInfo"] = new JsonObject { ["name"] = "fake-agent", ["version"] = "1.0.0" }
        };
    }

    private JsonObject NewSession(JsonObject parameters)
    {
        var sessionId = "fake-session-" + Interlocked.Increment(ref _nextSession);
        _sessionCwd[sessionId] = (string?)parameters["cwd"] ?? Environment.CurrentDirectory;

        var result = new JsonObject { ["sessionId"] = sessionId };
        if (_scenario["newSession"] is JsonObject extra)
        {
            foreach (var property in extra)
                result[property.Key] = property.Value?.DeepClone();
        }
        return result;
    }

    private async Task<JsonObject> LoadSessionAsync(JsonObject parameters)
    {
        if ((bool?)_scenario["failLoad"] == true)
            throw new RpcError(-32002, "Session not found");

        var sessionId = (string?)parameters["sessionId"] ?? "fake-session-loaded";
        _sessionCwd[sessionId] = (string?)parameters["cwd"] ?? Environment.CurrentDirectory;

        // Real agents replay the stored history as live notifications before answering.
        if (_scenario["replayOnLoad"] is JsonArray replay)
        {
            foreach (var update in replay)
            {
                if (update is not null)
                    SendUpdate(sessionId, update.DeepClone());
            }
        }

        await Task.Yield();
        return new JsonObject();
    }

    private async Task<JsonObject> PromptAsync(JsonObject parameters)
    {
        var sessionId = (string?)parameters["sessionId"] ?? "";
        var cwd = _sessionCwd.TryGetValue(sessionId, out var known) ? known : Environment.CurrentDirectory;

        var text = PromptText(parameters["prompt"] as JsonArray);
        var cancel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cancels[sessionId] = cancel;

        var turn = PickTurn(text);
        var steps = turn?["steps"] as JsonArray
            ?? new JsonArray(new JsonObject { ["say"] = "OK" });

        var stopReason = "end_turn";
        foreach (var step in steps.OfType<JsonObject>())
        {
            if (cancel.Task.IsCompleted && !step.ContainsKey("waitForCancel"))
            {
                stopReason = "cancelled";
                break;
            }

            var endWith = await RunStepAsync(sessionId, cwd, step, cancel).ConfigureAwait(false);
            if (endWith is not null)
            {
                stopReason = endWith;
                break;
            }
        }

        return new JsonObject { ["stopReason"] = stopReason };
    }

    private static string PromptText(JsonArray? prompt)
    {
        var sb = new StringBuilder();
        if (prompt is not null)
        {
            foreach (var block in prompt.OfType<JsonObject>())
            {
                if ((string?)block["type"] == "text")
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append((string?)block["text"]);
                }
            }
        }
        return sb.ToString();
    }

    private JsonObject? PickTurn(string promptText)
    {
        var turns = _scenario["turns"] as JsonArray;
        if (turns is null) return null;

        var marker = promptText.LastIndexOf(NextRequestMarker, StringComparison.Ordinal);
        var userText = marker >= 0 ? promptText.Substring(marker + NextRequestMarker.Length) : promptText;

        JsonObject? fallback = null;
        foreach (var turn in turns.OfType<JsonObject>())
        {
            var when = (string?)turn["when"];
            if (when is null) fallback ??= turn;
            else if (userText.Contains(when)) return turn;
        }
        return fallback;
    }

    /// <summary>Runs one step; a non-null result ends the turn with that stop reason.</summary>
    private async Task<string?> RunStepAsync(string sessionId, string cwd, JsonObject step, TaskCompletionSource<bool> cancel)
    {
        if (step.ContainsKey("say"))
        {
            var update = new JsonObject
            {
                ["sessionUpdate"] = "agent_message_chunk",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = (string?)step["say"] }
            };
            if (step["messageId"] is { } messageId) update["messageId"] = messageId.DeepClone();
            SendUpdate(sessionId, update);
        }
        else if (step.ContainsKey("think"))
        {
            var update = new JsonObject
            {
                ["sessionUpdate"] = "agent_thought_chunk",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = (string?)step["think"] }
            };
            if (step["messageId"] is { } messageId) update["messageId"] = messageId.DeepClone();
            SendUpdate(sessionId, update);
        }
        else if (step["update"] is JsonNode rawUpdate)
        {
            // "{cwd}" in any string stands for the session's working folder (for absolute paths).
            var escapedCwd = JsonValue.Create(cwd)!.ToJsonString().Trim('"');
            SendUpdate(sessionId, JsonNode.Parse(rawUpdate.ToJsonString().Replace("{cwd}", escapedCwd))!);
        }
        else if (step["write"] is JsonObject write)
        {
            var response = await RequestClientAsync("fs/write_text_file", new JsonObject
            {
                ["sessionId"] = sessionId,
                ["path"] = Resolve(cwd, (string?)write["path"] ?? ""),
                ["content"] = (string?)write["content"] ?? ""
            }).ConfigureAwait(false);
            Log("response", "fs/write_text_file", response);
        }
        else if (step.ContainsKey("read"))
        {
            var response = await RequestClientAsync("fs/read_text_file", new JsonObject
            {
                ["sessionId"] = sessionId,
                ["path"] = Resolve(cwd, (string?)step["read"] ?? "")
            }).ConfigureAwait(false);
            Log("response", "fs/read_text_file", response);
        }
        else if (step["disk"] is JsonObject disk)
        {
            var path = Resolve(cwd, (string?)disk["path"] ?? "");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, (string?)disk["content"] ?? "", new UTF8Encoding(false));
        }
        else if (step.ContainsKey("deleteDisk"))
        {
            var path = Resolve(cwd, (string?)step["deleteDisk"] ?? "");
            if (File.Exists(path)) File.Delete(path);
        }
        else if (step["permission"] is JsonObject permission)
        {
            var title = (string?)permission["title"] ?? "Permission";
            var response = await RequestClientAsync("session/request_permission", new JsonObject
            {
                ["sessionId"] = sessionId,
                ["toolCall"] = new JsonObject { ["toolCallId"] = "perm-" + Guid.NewGuid().ToString("N"), ["title"] = title },
                ["options"] = permission["options"]?.DeepClone() ?? DefaultPermissionOptions()
            }).ConfigureAwait(false);
            Log("event", "permissionResult", new JsonObject { ["title"] = title, ["response"] = (response["result"] ?? response["error"])?.DeepClone() });
        }
        else if (step.ContainsKey("delayMs"))
        {
            await Task.Delay((int?)step["delayMs"] ?? 0).ConfigureAwait(false);
        }
        else if (step.ContainsKey("waitForCancel"))
        {
            await Task.WhenAny(cancel.Task, Task.Delay(30000)).ConfigureAwait(false);
            Log("event", "cancelObserved", JsonValue.Create(cancel.Task.IsCompleted));
            return "cancelled";
        }
        else if (step.ContainsKey("fail"))
        {
            throw new RpcError(-32000, (string?)step["fail"] ?? "failed");
        }
        else if (step.ContainsKey("stopReason"))
        {
            return (string?)step["stopReason"] ?? "end_turn";
        }
        else if (step.ContainsKey("exit"))
        {
            Environment.Exit((int?)step["exit"] ?? 1);
        }
        else if (step.ContainsKey("hang"))
        {
            Log("event", "hung", null);
            _hung = true;
            await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
        }
        else
        {
            throw new RpcError(-32602, "Unknown step: " + step.ToJsonString());
        }

        return null;
    }

    private static JsonArray DefaultPermissionOptions() => new(
        new JsonObject { ["optionId"] = "allow", ["name"] = "Allow", ["kind"] = "allow_once" },
        new JsonObject { ["optionId"] = "reject", ["name"] = "Reject", ["kind"] = "reject_once" });

    private static string Resolve(string cwd, string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(cwd, path));

    private async Task<JsonObject> RequestClientAsync(string method, JsonObject parameters)
    {
        var id = Interlocked.Increment(ref _nextClientRequestId);
        var pending = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingClientRequests[id.ToString()] = pending;

        Log("out", method, parameters);
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });

        var finished = await Task.WhenAny(pending.Task, Task.Delay(30000)).ConfigureAwait(false);
        if (finished != pending.Task)
            throw new RpcError(-32000, "Timed out waiting for the client to answer " + method);

        return await pending.Task.ConfigureAwait(false);
    }

    private void SendUpdate(string sessionId, JsonNode update)
    {
        var parameters = new JsonObject { ["sessionId"] = sessionId, ["update"] = update };
        Log("out", "session/update", parameters);
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "session/update", ["params"] = parameters });
    }

    private void SendError(JsonNode id, int code, string message) =>
        Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        });

    private void Send(JsonObject message)
    {
        if (_hung) return;
        var line = message.ToJsonString();
        lock (_writeLock)
        {
            _output.WriteLine(line);
        }
    }

    private void Log(string direction, string name, JsonNode? payload)
    {
        if (_logPath is null) return;

        var entry = new JsonObject
        {
            ["dir"] = direction,
            ["name"] = name,
            ["payload"] = payload?.DeepClone()
        };

        lock (_logLock)
        {
            using var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = new UTF8Encoding(false).GetBytes(entry.ToJsonString() + "\n");
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
