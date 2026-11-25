// 回答的 Markdown 渲染。模型输出里的原始 HTML 一律转义, 链接只放行 http(s)。
import { Marked, type Tokens } from "marked";

const esc = (s: string) =>
  s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

const md = new Marked({ gfm: true, breaks: true });

md.use({
  renderer: {
    html(token: Tokens.HTML | Tokens.Tag) {
      return esc(token.text);
    },
    link(token: Tokens.Link) {
      const text = this.parser.parseInline(token.tokens);
      if (!/^https?:\/\//i.test(token.href)) return text;
      const title = token.title ? ` title="${esc(token.title)}"` : "";
      return `<a href="${esc(token.href)}"${title} target="_blank" rel="noopener noreferrer">${text}</a>`;
    },
    image(token: Tokens.Image) {
      return esc(token.text || "");
    },
  },
});

export function renderMarkdown(text: string): string {
  const html = md.parse(text, { async: false }) as string;
  // 表格包一层, 宽表格横向滚动而不是撑破面板
  return html.replace(/<table>/g, '<div class="md-table"><table>').replace(/<\/table>/g, "</table></div>");
}
