import type { HostMessage, WebMessage } from './protocol';

interface WebViewBridge {
  postMessage(message: string): void;
  /** WebView2: the host receives the objects (CoreWebView2File with the real path for dropped files). */
  postMessageWithAdditionalObjects(message: string, objects: unknown[]): void;
  addEventListener(type: 'message', listener: (e: { data: unknown }) => void): void;
}

declare global {
  interface Window {
    chrome?: { webview?: WebViewBridge };
  }
}

/** Sends a message to the VS host (JSON string, same convention as the existing `openFile`/`revert`). */
export function post(message: WebMessage): void {
  window.chrome?.webview?.postMessage(JSON.stringify(message));
}

/** Like `post`, but also hands the host the dropped File objects so it can read their paths. */
export function postWithFiles(message: WebMessage, files: File[]): void {
  const bridge = window.chrome?.webview;
  if (!bridge) return;
  if (files.length > 0) bridge.postMessageWithAdditionalObjects(JSON.stringify(message), files);
  else bridge.postMessage(JSON.stringify(message));
}

/** Subscribes to host messages. The dev harness and the e2e tests provide a `window.chrome.webview` shim. */
export function onHostMessage(handler: (message: HostMessage) => void): void {
  window.chrome?.webview?.addEventListener('message', (e) => {
    const data = e.data;
    if (data && typeof data === 'object' && typeof (data as { type?: unknown }).type === 'string') {
      handler(data as HostMessage);
    }
  });
}
