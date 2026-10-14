import { useCallback, useEffect, useState } from 'react'
import { applyTheme, readThemePreference, resolveTheme, type ThemePreference } from '../lib/theme'
import { IconMoon, IconSun } from './Icons'

export function ThemeToggle({ className }: { className?: string }) {
  const [pref, setPref] = useState<ThemePreference>(() => readThemePreference())
  const [systemDark, setSystemDark] = useState(() => resolveTheme('system') === 'dark')

  useEffect(() => {
    const mq = matchMedia('(prefers-color-scheme: dark)')
    const onChange = (e: MediaQueryListEvent) => setSystemDark(e.matches)
    mq.addEventListener('change', onChange)
    return () => mq.removeEventListener('change', onChange)
  }, [])

  const effective = pref === 'system' ? (systemDark ? 'dark' : 'light') : pref

  const toggle = useCallback(() => {
    const next: ThemePreference = effective === 'dark' ? 'light' : 'dark'
    // Choosing the system's current value again means "follow the system".
    const normalized: ThemePreference = next === (systemDark ? 'dark' : 'light') ? 'system' : next
    applyTheme(normalized)
    setPref(normalized)
  }, [effective, systemDark])

  return (
    <button
      type="button"
      className={className ?? 'btn btn-sm btn-quiet'}
      onClick={toggle}
      aria-label={effective === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
      title={effective === 'dark' ? 'Light theme' : 'Dark theme'}
    >
      {effective === 'dark' ? <IconSun /> : <IconMoon />}
      <span className="sr">{effective === 'dark' ? 'Light theme' : 'Dark theme'}</span>
    </button>
  )
}
