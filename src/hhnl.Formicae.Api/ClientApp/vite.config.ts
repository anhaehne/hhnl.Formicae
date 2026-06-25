import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "../wwwroot",
    emptyOutDir: true
  },
  server: {
    proxy: {
      "/api": "http://localhost:5000",
      "/healthz": "http://localhost:5000"
    }
  }
});
