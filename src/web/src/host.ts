// 宿主: 在狐表里走 WebView2 的消息通道; 在浏览器里 (npm run dev) 换成模拟宿主, 界面调试不用启动狐表。
import type { HostEvent, WebMessage } from "./protocol";
import { createMockHost } from "./mockHost";

export interface Host {
  post(msg: WebMessage): void;
  subscribe(cb: (evt: HostEvent) => void): () => void;
  readonly mock: boolean;
}

interface WebViewBridge {
  postMessage(msg: unknown): void;
  addEventListener(type: "message", cb: (e: MessageEvent) => void): void;
  removeEventListener(type: "message", cb: (e: MessageEvent) => void): void;
}

function webview(): WebViewBridge | undefined {
  return (window as unknown as { chrome?: { webview?: WebViewBridge } }).chrome?.webview;
}

function createWebViewHost(wv: WebViewBridge): Host {
  return {
    mock: false,
    post: (msg) => wv.postMessage(msg),
    subscribe(cb) {
      const handler = (e: MessageEvent) => {
        const data = typeof e.data === "string" ? JSON.parse(e.data) : e.data;
        cb(data as HostEvent);
      };
      wv.addEventListener("message", handler);
      return () => wv.removeEventListener("message", handler);
    },
  };
}

export const host: Host = webview() ? createWebViewHost(webview()!) : createMockHost();
