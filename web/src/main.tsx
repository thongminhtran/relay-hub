import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App.tsx'
import { applyTheme, readThemePreference } from './lib/theme'
import { isMockEnabled } from './mock/flag'
import './styles.css'

applyTheme(readThemePreference())

async function boot() {
  if (isMockEnabled()) {
    // Dev-only in-browser API: never part of the normal bundle path, loaded on demand.
    const { installMockApi } = await import('./mock/install')
    installMockApi()
  }
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <App />
    </StrictMode>,
  )
}

void boot()
