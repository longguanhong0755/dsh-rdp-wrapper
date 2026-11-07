// Opt-in load-time tracing for plugin diagnosis (DSH_RDP_TRACE=1).
import fs from 'node:fs'
import path from 'node:path'
const LOGDIR = path.join(process.env['ProgramData'] || 'C:' + String.fromCharCode(92) + 'ProgramData', 'dsh-rdp-wrapper', 'logs')
export function trace(msg) {
  if (process.env['DSH_RDP_TRACE'] !== '1') return
  try {
    fs.mkdirSync(LOGDIR, { recursive: true })
    fs.appendFileSync(path.join(LOGDIR, 'plugin-load-trace.log'), new Date().toISOString() + ' ' + msg + String.fromCharCode(10))
  } catch { }
}
