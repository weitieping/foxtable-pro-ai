import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { viteSingleFile } from "vite-plugin-singlefile";

// 打成一个自包含的 index.html, 由 DLL 作为嵌入资源提供, 不依赖 CDN
export default defineConfig({
  plugins: [react(), viteSingleFile()],
  build: { target: "es2022", outDir: "dist", emptyOutDir: true, reportCompressedSize: false },
});
