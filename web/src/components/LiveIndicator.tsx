import { useRealtime } from '../hooks/useRealtime'
import { IconRefresh } from './Icons'

/**
 * Green dot while the SignalR hub is connected. Anything else becomes a grey "Live updates unavailable — Refresh"
 * button: refetching is the user's decision, never a timer.
 */
export function LiveIndicator() {
  const { state, error, refresh, refreshing } = useRealtime()

  if (state === 'connected') {
    return (
      <span className="live is-on" title="SignalR connected — new messages appear without refreshing">
        Live
      </span>
    )
  }
  if (state === 'connecting' || state === 'reconnecting') {
    return (
      <span className="live is-busy" title={state === 'reconnecting' ? 'Connection dropped, reconnecting…' : 'Connecting live updates…'}>
        {state === 'reconnecting' ? 'Reconnecting…' : 'Connecting…'}
      </span>
    )
  }
  const reason = state === 'unavailable' ? (error ?? 'Realtime is not configured on this API') : (error ?? 'Connection lost')
  return (
    <button type="button" className="live is-off" onClick={() => void refresh()} disabled={refreshing} title={`${reason}. Click to reload the data.`}>
      {refreshing ? <span className="spinner" /> : <IconRefresh />}
      Live updates unavailable — Refresh
    </button>
  )
}
