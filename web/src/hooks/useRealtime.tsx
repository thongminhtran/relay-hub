import { useQueryClient } from '@tanstack/react-query'
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useAuth } from '../lib/auth'
import { keys } from '../lib/queries'
import { realtime, type RealtimeState } from '../lib/realtime'
import type { ConversationDetail, EventListItem, Paged } from '../lib/types'

interface RealtimeContextValue {
  state: RealtimeState
  error: string | null
  /** Manual refresh: refetch every active query and try the hub again. The only alternative to live updates — never a timer. */
  refresh: () => Promise<void>
  refreshing: boolean
}

const RealtimeContext = createContext<RealtimeContextValue | null>(null)

/** Connects the SignalR client while logged in and turns hub messages into react-query cache updates. */
export function RealtimeProvider({ children }: { children: ReactNode }) {
  const { isAuthenticated } = useAuth()
  const queryClient = useQueryClient()
  const [state, setState] = useState<RealtimeState>(realtime.getState())
  const [error, setError] = useState<string | null>(realtime.getLastError())
  const [refreshing, setRefreshing] = useState(false)

  useEffect(() => {
    const unsub = realtime.subscribe((s, e) => {
      setState(s)
      setError(e)
    })
    return unsub
  }, [])

  useEffect(() => {
    if (!isAuthenticated) {
      void realtime.disconnect()
      return
    }
    void realtime.connect()
    return () => {
      void realtime.disconnect()
    }
  }, [isAuthenticated])

  useEffect(() => {
    const offCreated = realtime.on('eventCreated', () => {
      void queryClient.invalidateQueries({ queryKey: keys.events })
      void queryClient.invalidateQueries({ queryKey: keys.conversations })
      void queryClient.invalidateQueries({ queryKey: keys.stats })
    })

    const offDelivery = realtime.on('deliveryUpdated', (msg) => {
      // Patch the row in any cached list page, then let the detail page refetch its attempts.
      queryClient.setQueriesData<Paged<EventListItem>>({ queryKey: [...keys.events, 'list'] }, (old) => {
        if (!old) return old
        let touched = false
        const items = old.items.map((ev) => {
          if (ev.id !== msg.eventId) return ev
          touched = true
          return {
            ...ev,
            deliveries: ev.deliveries.map((d) =>
              d.id === msg.deliveryId ? { ...d, status: msg.status, attemptCount: msg.attemptCount, lastError: msg.lastError } : d,
            ),
          }
        })
        return touched ? { ...old, items } : old
      })
      void queryClient.invalidateQueries({ queryKey: keys.event(msg.eventId) })
      void queryClient.invalidateQueries({ queryKey: keys.stats })
    })

    const offReply = realtime.on('replySent', (msg) => {
      queryClient.setQueryData<ConversationDetail>(keys.conversation(msg.conversationId), (old) => {
        if (!old) return old
        if (old.messages.some((m) => m.id === msg.eventId)) return old
        return {
          ...old,
          lastOutboundAt: msg.at,
          messages: [
            ...old.messages,
            { id: msg.eventId, direction: 'out', text: msg.text, at: msg.at, providerMessageId: msg.providerMessageId, correlationId: null },
          ],
        }
      })
      void queryClient.invalidateQueries({ queryKey: [...keys.conversations, 'list'] })
      void queryClient.invalidateQueries({ queryKey: keys.events })
      void queryClient.invalidateQueries({ queryKey: keys.stats })
    })

    return () => {
      offCreated()
      offDelivery()
      offReply()
    }
  }, [queryClient])

  const refresh = useCallback(async () => {
    setRefreshing(true)
    try {
      await Promise.all([queryClient.invalidateQueries(), realtime.getState() === 'connected' ? Promise.resolve() : realtime.connect()])
    } finally {
      setRefreshing(false)
    }
  }, [queryClient])

  const value = useMemo<RealtimeContextValue>(() => ({ state, error, refresh, refreshing }), [state, error, refresh, refreshing])
  return <RealtimeContext.Provider value={value}>{children}</RealtimeContext.Provider>
}

export function useRealtime(): RealtimeContextValue {
  const ctx = useContext(RealtimeContext)
  if (!ctx) throw new Error('useRealtime must be used inside <RealtimeProvider>')
  return ctx
}
