// 读用户选的文件, 转成发给 DLL 的 attach 消息。图片先缩小 (长边不超过 1600, JPEG), 省流量也省模型的 token。
import type { Config, WebMessage } from "./protocol";

const MAX_SIDE = 1600;
const MAX_FILE = 10 * 1024 * 1024;
const SHEET = /\.(xlsx|xls|csv)$/i;

export async function readAttachment(f: File, config: Config): Promise<WebMessage> {
  if (f.type.startsWith("image/")) {
    if (!config.images) throw new Error("这个助手不支持发图片");
    return { type: "attach", name: f.name || "图片", kind: "image", data: await shrinkImage(f) };
  }
  if (SHEET.test(f.name)) {
    if (!config.files) throw new Error("这个助手不支持发文件");
    if (f.size > MAX_FILE) throw new Error("文件超过 10 MB");
    return { type: "attach", name: f.name, kind: "file", data: await base64(f) };
  }
  throw new Error(config.files ? "只支持图片和 Excel、CSV 文件" : "只支持图片");
}

function base64(f: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => {
      const s = String(r.result);
      resolve(s.slice(s.indexOf(",") + 1));
    };
    r.onerror = () => reject(new Error("读不了这个文件"));
    r.readAsDataURL(f);
  });
}

async function shrinkImage(f: File): Promise<string> {
  const url = URL.createObjectURL(f);
  try {
    const img = await new Promise<HTMLImageElement>((resolve, reject) => {
      const i = new Image();
      i.onload = () => resolve(i);
      i.onerror = () => reject(new Error("认不出这张图片"));
      i.src = url;
    });
    const scale = Math.min(1, MAX_SIDE / Math.max(img.naturalWidth, img.naturalHeight));
    const w = Math.max(1, Math.round(img.naturalWidth * scale));
    const h = Math.max(1, Math.round(img.naturalHeight * scale));
    const canvas = document.createElement("canvas");
    canvas.width = w;
    canvas.height = h;
    const ctx = canvas.getContext("2d")!;
    ctx.fillStyle = "#fff"; // 透明底的 PNG 转 JPEG 时不变黑
    ctx.fillRect(0, 0, w, h);
    ctx.drawImage(img, 0, 0, w, h);
    return canvas.toDataURL("image/jpeg", 0.85);
  } finally {
    URL.revokeObjectURL(url);
  }
}
