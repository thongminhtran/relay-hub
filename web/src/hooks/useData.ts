import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { fetchers, keys, type ConversationFilters, type EventFilters } from '../lib/queries'

export function useEvents(filters: EventFilters) {
  return useQuery({
    queryKey: keys.eventList(filters),
    queryFn: ({ signal }) => fetchers.events(filters, signal),
    placeholderData: keepPreviousData,
  })
}

export function useEvent(id: number | null) {
  return useQuery({
    queryKey: keys.event(id ?? -1),
    queryFn: ({ signal }) => fetchers.event(id as number, signal),
    enabled: id !== null && Number.isFinite(id),
  })
}

export function useConversations(filters: ConversationFilters) {
  return useQuery({
    queryKey: keys.conversationList(filters),
    queryFn: ({ signal }) => fetchers.conversations(filters, signal),
    placeholderData: keepPreviousData,
  })
}

export function useConversation(id: number | null) {
  return useQuery({
    queryKey: keys.conversation(id ?? -1),
    queryFn: ({ signal }) => fetchers.conversation(id as number, signal),
    enabled: id !== null && Number.isFinite(id),
  })
}

export function useStats() {
  return useQuery({ queryKey: keys.stats, queryFn: ({ signal }) => fetchers.stats(signal) })
}

export function useSources() {
  return useQuery({ queryKey: keys.sources, queryFn: ({ signal }) => fetchers.sources(signal) })
}
export function useDestinations() {
  return useQuery({ queryKey: keys.destinations, queryFn: ({ signal }) => fetchers.destinations(signal) })
}
export function useRoutes() {
  return useQuery({ queryKey: keys.routes, queryFn: ({ signal }) => fetchers.routes(signal) })
}
