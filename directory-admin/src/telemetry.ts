import { faro, getWebInstrumentations, initializeFaro, LogLevel } from '@grafana/faro-web-sdk'

/*
  Browser telemetry with Grafana Faro, sent to Grafana Alloy and on to Loki (see monitoring/alloy.alloy).

  The server already sees every request that reaches it. What it can't see is what goes wrong in the
  browser: JavaScript errors, an API that's unreachable or blocked by CORS, slow page loads. That's
  what this reports, so a "the dashboard is broken" call can be checked in Grafana.

  Privacy: this is a law firm, and the screens show client names. So we never attach the user's
  identity, only capture console warnings and errors, and strip query strings (search terms) from
  the paths we report. Telemetry is off unless VITE_FARO_URL is set at build time.
*/

/** Drop the query string: it can hold search terms such as client names. */
const pathOnly = (path: string) => path.split('?')[0]

/**
 * Starts Grafana Faro if VITE_FARO_URL was set at build time; otherwise does nothing.
 * @param appName Shown in Grafana as service_name, e.g. 'directory-web'.
 */
export function initTelemetry(appName: string): void {
  const url = import.meta.env.VITE_FARO_URL as string | undefined
  if (!url) return

  initializeFaro({
    url,
    app: {
      name: appName,
      version: (import.meta.env.VITE_APP_VERSION as string | undefined) ?? 'dev',
      environment: import.meta.env.MODE,
    },
    // JS errors, unhandled promise rejections, web vitals, page views and an anonymous session id.
    // Performance (resource timing) is off: it reports every fetch with its full URL, query string
    // included, and our client search puts client names in the query string.
    instrumentations: getWebInstrumentations({ enablePerformanceInstrumentation: false }),
    consoleInstrumentation: {
      disabledLevels: [LogLevel.TRACE, LogLevel.DEBUG, LogLevel.LOG, LogLevel.INFO],
    },
    // Belt and braces: never send a query string as part of the page URL.
    beforeSend: (item) => {
      const page = item.meta.page
      if (page?.url) page.url = pathOnly(page.url)
      return item
    },
  })
}

/** The request never reached the API (down, wrong URL, CORS). The server has no record of these. */
export function reportApiUnreachable(api: string, path: string): void {
  faro.api?.pushEvent('api_unreachable', { api, path: pathOnly(path) })
}

/** The API answered with a server error. 4xx are expected outcomes (validation, permissions), so not reported. */
export function reportApiError(api: string, path: string, status: number): void {
  if (status >= 500) faro.api?.pushEvent('api_error', { api, path: pathOnly(path), status: String(status) })
}
