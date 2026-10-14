export type ThemePreference = 'light' | 'dark' | 'system'
export const THEME_STORAGE_KEY = 'relayhub.theme'

export function readThemePreference(): ThemePreference {
  try {
    const v = localStorage.getItem(THEME_STORAGE_KEY)
    return v === 'light' || v === 'dark' ? v : 'system'
  } catch {
    return 'system'
  }
}

/** Applies the preference to <html data-theme> — CSS handles `system` through prefers-color-scheme. */
export function applyTheme(pref: ThemePreference): void {
  const root = document.documentElement
  if (pref === 'system') root.removeAttribute('data-theme')
  else root.setAttribute('data-theme', pref)
  try {
    if (pref === 'system') localStorage.removeItem(THEME_STORAGE_KEY)
    else localStorage.setItem(THEME_STORAGE_KEY, pref)
  } catch {
    /* storage blocked: the attribute still applies for this page load */
  }
}

/** The theme actually in effect right now. */
export function resolveTheme(pref: ThemePreference): 'light' | 'dark' {
  if (pref !== 'system') return pref
  return typeof matchMedia === 'function' && matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
}
