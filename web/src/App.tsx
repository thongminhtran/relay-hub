import { QueryClientProvider } from '@tanstack/react-query'
import { useState } from 'react'
import { BrowserRouter, Route, Routes } from 'react-router-dom'
import { AppShell } from './components/AppShell'
import { RequireAuth } from './components/RequireAuth'
import { Toasts } from './components/Toasts'
import { RealtimeProvider } from './hooks/useRealtime'
import { AuthProvider } from './lib/auth'
import { createQueryClient } from './lib/queries'
import { ToastProvider } from './lib/toast'
import { ConversationsPage } from './pages/Conversations'
import { ConversationThreadPage } from './pages/ConversationThread'
import { EventDetailPage } from './pages/EventDetail'
import { InboxPage } from './pages/Inbox'
import { LandingPage } from './pages/Landing'
import { LoginPage } from './pages/Login'
import { NotFoundPage } from './pages/NotFound'
import { SettingsPage } from './pages/Settings'
import { DestinationsTab } from './pages/settings/DestinationsTab'
import { RoutesTab } from './pages/settings/RoutesTab'
import { SourcesTab } from './pages/settings/SourcesTab'
import { StatsPage } from './pages/Stats'
import { WakePage } from './pages/Wake'

export default function App() {
  const [queryClient] = useState(createQueryClient)
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <ToastProvider>
          <AuthProvider>
            <Routes>
              <Route path="/" element={<LandingPage />} />
              <Route path="/login" element={<LoginPage />} />
              <Route path="/app">
                <Route index element={<WakePage />} />
                <Route element={<RequireAuth />}>
                  <Route
                    element={
                      <RealtimeProvider>
                        <AppShell />
                      </RealtimeProvider>
                    }
                  >
                    <Route path="inbox" element={<InboxPage />} />
                    <Route path="events/:id" element={<EventDetailPage />} />
                    <Route path="conversations" element={<ConversationsPage />} />
                    <Route path="conversations/:id" element={<ConversationThreadPage />} />
                    <Route path="stats" element={<StatsPage />} />
                    <Route path="settings" element={<SettingsPage />}>
                      <Route path="sources" element={<SourcesTab />} />
                      <Route path="destinations" element={<DestinationsTab />} />
                      <Route path="routes" element={<RoutesTab />} />
                    </Route>
                  </Route>
                </Route>
              </Route>
              <Route path="*" element={<NotFoundPage />} />
            </Routes>
            <Toasts />
          </AuthProvider>
        </ToastProvider>
      </BrowserRouter>
    </QueryClientProvider>
  )
}
