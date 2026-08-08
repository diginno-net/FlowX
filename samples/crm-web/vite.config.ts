import { fileURLToPath, URL } from 'node:url'
import { lingui } from '@lingui/vite-plugin'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The sample's ASP.NET host. Proxied rather than pointed at directly so the browser
// treats the API as same-origin: the CORS policy in Program.cs is what a deployed
// client needs, and a dev server that depended on it would hide a broken one.
const API = process.env.CRM_API ?? 'http://localhost:5000'

export default defineConfig({
  plugins: [
    // The macro rewrites `t`…`` into a catalogue lookup at build time, so no string is
    // resolved by scanning at run time.
    react({ babel: { plugins: ['@lingui/babel-plugin-lingui-macro'] } }),
    lingui(),
  ],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: API, changeOrigin: true },

      // The compiler's manifest, which the setup screen lists the application's flows from. It
      // is an application-level document rather than a CRM one, so it is not under /api and has
      // to be named here — a proxy that only forwards /api leaves it 404ing against Vite.
      '/flowx.manifest.json': { target: API, changeOrigin: true },
    },
  },
})
