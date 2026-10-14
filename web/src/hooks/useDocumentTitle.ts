import { useEffect } from 'react'

const SUFFIX = 'RelayHub'

/** Sets `document.title` to "<title> · RelayHub" (or just "RelayHub" when empty) while the component is mounted. */
export function useDocumentTitle(title: string | null | undefined): void {
  useEffect(() => {
    const previous = document.title
    document.title = title ? `${title} · ${SUFFIX}` : SUFFIX
    return () => {
      document.title = previous
    }
  }, [title])
}
