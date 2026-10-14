# RelayHub dashboard (`web/`)

React 19 + TypeScript + Vite single-page app for the RelayHub API. Routes, environment variables and the mock API are
documented in `../docs/HANDOFF.md` ("Week 3 — dashboard").

```bash
npm install          # once (or after adding a dependency)
npm run dev          # http://localhost:5173 against VITE_API_BASE_URL (default http://localhost:5080)
npm run dev -- --open '/?mock=1'   # same, against the in-browser mock API (no backend needed)
npm test             # vitest + Testing Library
npm run lint         # oxlint
npm run build        # tsc -b + vite build → dist/ (deployed to Azure Static Web Apps)
```

Copy `.env.example` to `.env.local` to point the dashboard at another API.
