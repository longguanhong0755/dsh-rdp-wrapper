// Drive a single plugin tool's execute() directly — same code path the dsh
// agent uses — without needing the harness. Usage:
//   node test/drive-tools.mjs <toolName> ['<json args>']
import { buildToolDefs, buildSessionToolDefs } from '../lib/tools.js'

const ctx = { get: () => null }
const defs = [...buildToolDefs(ctx), ...buildSessionToolDefs(ctx)]
const byName = new Map(defs.map(d => [d.name, d]))

const [,, cmd, ...rest] = process.argv
const def = byName.get(cmd)
if (!def) {
  console.error('unknown tool: ' + cmd + '; available: ' + [...byName.keys()].join(' '))
  process.exit(1)
}
const args = rest.length ? JSON.parse(rest.join(' ')) : {}
try {
  const r = await def.execute(args, {})
  console.log(JSON.stringify(r, null, 2))
} catch (e) {
  console.error('ERROR: ' + (e && e.stack || e))
  process.exit(1)
}
