/**
 * Which language the client speaks, and how it changes.
 *
 * ONE ACTIVATION POINT. Catalogues are loaded on demand and `i18n.activate` is called in exactly
 * one place, so a screen never has to know a locale exists. A component that wants a translated
 * string asks the macro; a component that wants to *change* the language calls `setLocale`.
 *
 * THE CHOICE OUTLIVES THE TAB. It is written to `localStorage` and read back before the first
 * render, because a language that resets on refresh is one a person stops trusting and starts
 * working around. `document.documentElement.lang` is set with it: without that, a screen reader
 * announces Vietnamese text with an English voice, and `:lang()` styling has nothing to match.
 *
 * NO AUTOMATIC DETECTION FROM `navigator.language`. A Vietnamese person working in an
 * English-language CRM is the normal case, not the exception, and guessing from the browser makes
 * that person opt out on every new device. English stays the default and the choice is explicit.
 */
import { i18n } from '@lingui/core'

export const LOCALES = {
  en: 'English',
  vi: 'Tiếng Việt',
} as const

export type Locale = keyof typeof LOCALES

export const DEFAULT_LOCALE: Locale = 'en'

const STORAGE_KEY = 'crm.locale'

export function isLocale(value: string | null): value is Locale {
  return value !== null && Object.hasOwn(LOCALES, value)
}

/** What the person chose last time, or the default. Never throws: private mode has no storage. */
export function storedLocale(): Locale {
  try {
    const saved = localStorage.getItem(STORAGE_KEY)
    return isLocale(saved) ? saved : DEFAULT_LOCALE
  } catch {
    return DEFAULT_LOCALE
  }
}

/**
 * Loads a catalogue and makes it current.
 *
 * The dynamic import is what keeps Vietnamese out of an English bundle: Vite splits one chunk per
 * locale, and a client that never switches never downloads the other one.
 */
export async function activateLocale(locale: Locale): Promise<void> {
  const { messages } = await import(`../locales/${locale}.po`)

  i18n.loadAndActivate({ locale, messages })
  document.documentElement.lang = locale

  try {
    localStorage.setItem(STORAGE_KEY, locale)
  } catch {
    // A browser that refuses storage still gets the language for this session.
  }
}

export { i18n }
