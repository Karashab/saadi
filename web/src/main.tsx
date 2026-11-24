import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import '@fontsource-variable/vazirmatn';
import './styles.css';
import App from './App';
import { ApiError } from './api';
import { ToastProvider } from './ui';

const client = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 15000,
      refetchOnWindowFocus: true,
      retry: (count, error) => !(error instanceof ApiError && error.status < 500) && count < 2,
    },
  },
});

import { initAuth } from './auth';

initAuth().catch(() => 'failed' as const).then(state => createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={client}>
      <ToastProvider><App authFailed={state === 'failed'} /></ToastProvider>
    </QueryClientProvider>
  </StrictMode>,
)).then(() => void 0, () => void 0);
