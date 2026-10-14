import { useEffect, useState } from 'react'

/** Returns `value` after it has stopped changing for `delayMs` (default 300 ms). */
export function useDebounce<T>(value: T, delayMs = 300): T {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    const id = window.setTimeout(() => setDebounced(value), delayMs)
    return () => window.clearTimeout(id)
  }, [value, delayMs])
  return debounced
}
