import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import './styles.css'
import { initTelemetry } from './telemetry'

// Before rendering, so errors during the first render are captured too.
initTelemetry('directory-web')

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
