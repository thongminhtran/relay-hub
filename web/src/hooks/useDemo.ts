import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query'
import { isApiError } from '../lib/api'
import { fetchers, keys, mutations } from '../lib/queries'
import { useToast } from '../lib/toast'
import type { DemoSimulateRequest } from '../lib/types'

/**
 * Without SignalR (local dev) a simulated message only shows up once the worker stored it. This is NOT polling: it is
 * exactly two one-shot follow-ups after the user's own action — one at ~4 s (typical worker latency) and one at ~10 s.
 */
export const SIMULATE_FOLLOW_UP_MS: readonly [number, number] = [4000, 10000]

export function scheduleSimulateFollowUp(queryClient: QueryClient, delays: readonly [number, number] = SIMULATE_FOLLOW_UP_MS): void {
  for (const ms of delays) {
    window.setTimeout(() => {
      void queryClient.invalidateQueries({ queryKey: keys.events })
      void queryClient.invalidateQueries({ queryKey: keys.conversations })
      void queryClient.invalidateQueries({ queryKey: keys.stats })
    }, ms)
  }
}

export const RATE_LIMIT_MESSAGE = 'The simulator allows 20 messages per minute. Give it a moment and try again.'

export function useDemoFailureMode(enabled = true) {
  return useQuery({ queryKey: keys.demoFailureMode, queryFn: ({ signal }) => fetchers.demoFailureMode(signal), enabled })
}

export function useSimulate() {
  const queryClient = useQueryClient()
  const toast = useToast()
  return useMutation({
    mutationFn: (body: DemoSimulateRequest) => mutations.demoSimulate(body),
    onSuccess: (res) => {
      toast.push('ok', `Simulated ${res.channel} message from ${res.sender.name}`, res.hint)
      scheduleSimulateFollowUp(queryClient)
    },
    onError: (e) => {
      if (isApiError(e) && e.status === 429) toast.push('warn', 'Slow down', RATE_LIMIT_MESSAGE)
      else toast.error(e, 'Simulation failed')
    },
  })
}

export function useSetFailureMode() {
  const queryClient = useQueryClient()
  const toast = useToast()
  return useMutation({
    mutationFn: (enabled: boolean) => mutations.demoSetFailureMode(enabled),
    onSuccess: (res) => {
      queryClient.setQueryData(keys.demoFailureMode, res)
      toast.push(res.enabled ? 'warn' : 'ok', res.enabled ? 'Failure mode on' : 'Failure mode off', res.enabled ? 'The Flaky endpoint now answers 503 to every website-form delivery.' : 'Deliveries to the Flaky endpoint succeed again — replay what dead-lettered.')
      void queryClient.invalidateQueries({ queryKey: keys.destinations })
    },
    onError: (e) => toast.error(e, 'Could not change failure mode'),
  })
}

export function useDemoReset() {
  const queryClient = useQueryClient()
  const toast = useToast()
  return useMutation({
    mutationFn: () => mutations.demoReset(),
    onSuccess: (res) => {
      toast.push('ok', 'Demo data reset', `${res.conversations} conversations and ${res.events} events re-seeded; failure mode off.`)
      void queryClient.invalidateQueries()
    },
    onError: (e) => toast.error(e, 'Reset failed'),
  })
}
