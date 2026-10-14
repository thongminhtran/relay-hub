export const MOCK_FLAG_KEY = 'relayhub.mock'

/**
 * The in-browser mock API is on when `VITE_MOCK_API=1` at build time, or when the page was opened with `?mock=1`
 * (remembered for the tab in sessionStorage; `?mock=0` turns it off again).
 */
export function isMockEnabled(): boolean {
  if (import.meta.env.VITE_MOCK_API === '1') return true
  if (typeof location === 'undefined') return false
  try {
    const flag = new URLSearchParams(location.search).get('mock')
    if (flag === '1') {
      sessionStorage.setItem(MOCK_FLAG_KEY, '1')
      return true
    }
    if (flag === '0') {
      sessionStorage.removeItem(MOCK_FLAG_KEY)
      return false
    }
    return sessionStorage.getItem(MOCK_FLAG_KEY) === '1'
  } catch {
    return false
  }
}
