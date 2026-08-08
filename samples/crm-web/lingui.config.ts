import { defineConfig } from '@lingui/cli'
import { formatter } from '@lingui/format-po'

/**
 * What the extractor reads and where the catalogues land.
 *
 * THE SOURCE TEXT IS THE KEY. `t`Convert a lead`` is both the English string and the catalogue
 * entry, so nobody invents `sales.console.convert_lead` and nobody has to keep that name honest.
 * The cost is stated rather than discovered: editing an English string orphans its translation,
 * which is why `msg` carries a `context` wherever the same word means two things — see
 * `src/lib/i18n.ts`.
 *
 * PO RATHER THAN JSON, because a `.po` entry carries the source reference and a translator
 * comment. A JSON catalogue is a flat map, and a flat map is where `Record → "Ghi lại"` comes
 * from: the translator never sees that the word is a noun on a record page.
 */
export default defineConfig({
  sourceLocale: 'en',
  locales: ['en', 'vi'],
  catalogs: [
    {
      path: '<rootDir>/src/locales/{locale}',
      include: ['src'],
      exclude: ['**/node_modules/**', '**/__tests__/**', '**/*.test.*'],
    },
  ],
  format: formatter({ lineNumbers: false }),
})
