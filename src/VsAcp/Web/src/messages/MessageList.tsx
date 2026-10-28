import { useEffect, useLayoutEffect, useRef } from 'preact/hooks';
import { MessageView } from './MessageView';
import { useChat } from '../chat';

const NEAR_BOTTOM_PX = 50;

/**
 * The scrolling list of chat messages. It follows new content (streaming text, new messages, images that
 * finish loading) unless the user has scrolled up to read, and jumps to the end when the list is replaced.
 */
export function MessageList() {
  const { order, resetCount } = useChat().messages;
  const ids = order.value;
  const reset = resetCount.value;
  const scroller = useRef<HTMLDivElement>(null);
  const inner = useRef<HTMLDivElement>(null);
  const follow = useRef(true);

  useEffect(() => {
    const el = scroller.current!;
    const onScroll = () => {
      follow.current = el.scrollHeight - el.scrollTop - el.clientHeight < NEAR_BOTTOM_PX;
    };
    el.addEventListener('scroll', onScroll, { passive: true });
    // any growth of the content (a streamed chunk, a rendered code block, a loaded image) re-pins the end
    const observer = new ResizeObserver(() => {
      if (follow.current) el.scrollTop = el.scrollHeight;
    });
    observer.observe(inner.current!);
    return () => {
      el.removeEventListener('scroll', onScroll);
      observer.disconnect();
    };
  }, []);

  // list replaced (load / clear): start following again and go to the end
  useLayoutEffect(() => {
    follow.current = true;
    const el = scroller.current!;
    el.scrollTop = el.scrollHeight;
  }, [reset]);

  return (
    <div class="message-list" ref={scroller}>
      <div class="message-list-inner" ref={inner}>
        {ids.map((id) => (
          <MessageView key={id} id={id} />
        ))}
      </div>
    </div>
  );
}
