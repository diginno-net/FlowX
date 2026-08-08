/**
 * The language, as state the whole application can read and one control can change.
 *
 * IT RENDERS NOTHING UNTIL A CATALOGUE IS LOADED. The alternative — render English, then swap —
 * shows every person who chose Vietnamese a flash of the language they did not choose, on every
 * refresh. The wait is one dynamic import against a chunk the browser has usually cached.
 */
import { I18nProvider } from '@lingui/react'
import { createContext, use, useEffect, useState } from 'react'
import type { ReactNode } from 'react'
import { activateLocale, i18n, storedLocale } from '@/lib/i18n'
import type { Locale } from '@/lib/i18n'

interface LocaleContextValue {
  locale: Locale
  setLocale: (locale: Locale) => void
}

const LocaleContext = createContext<LocaleContextValue | null>(null)

export function useLocale(): LocaleContextValue {
  const value = use(LocaleContext)

  if (!value) {
    throw new Error('useLocale must be used inside <LocaleProvider>.')
  }

  return value
}

export function LocaleProvider({ children }: { children: ReactNode }) {
  const [locale, setLocaleState] = useState<Locale>(storedLocale)
  const [ready, setReady] = useState(false)

  useEffect(() => {
    let cancelled = false

    void activateLocale(locale).then(() => {
      // A locale that changed while this import was in flight has its own effect already running;
      // letting this one finish would activate the catalogue the person just moved away from.
      if (!cancelled) setReady(true)
    })

    return () => {
      cancelled = true
    }
  }, [locale])

  if (!ready) return null

  return (
    <LocaleContext value={{ locale, setLocale: setLocaleState }}>
      <I18nProvider i18n={i18n}>{children}</I18nProvider>
    </LocaleContext>
  )
}
