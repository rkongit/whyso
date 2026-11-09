import { cpSync, mkdirSync, rmSync, existsSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const dist = join(here, '..', 'dist')
const wwwroot = join(here, '..', '..', 'src', 'Whyso.Ui', 'wwwroot')

if (!existsSync(dist)) {
  console.error('ui/dist missing — vite build failed?')
  process.exit(1)
}

rmSync(wwwroot, { recursive: true, force: true })
mkdirSync(wwwroot, { recursive: true })
cpSync(dist, wwwroot, { recursive: true })
console.log(`Copied ${dist} → ${wwwroot}`)
