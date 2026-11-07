// Dev helper: call the in-session bridge directly.
// usage: node test/rdp-rpc.mjs <sessionId|auto> <method> '<json-params>'
import net from 'node:net'
import fs from 'node:fs'
import path from 'node:path'

const PD = process.env['ProgramData'] || 'C:\\ProgramData'
const portsDir = path.join(PD, 'dsh-rdp-wrapper', 'ports')

function readJson(p) {
  try { return JSON.parse(fs.readFileSync(p, 'utf8')) } catch { return null }
}

function pickBridge(sid) {
  if (sid && sid !== 'auto') {
    const j = readJson(path.join(portsDir, `bridge-session-${sid}.json`))
    if (!j) throw new Error('no bridge for session ' + sid)
    return j
  }
  const host = readJson(path.join(portsDir, 'childhost.json'))
  if (!host || !host.childSessionId) throw new Error('no childhost.json')
  const j = readJson(path.join(portsDir, `bridge-session-${host.childSessionId}.json`))
  if (!j) throw new Error('no bridge for child session ' + host.childSessionId)
  return j
}

function call(bridge, method, params) {
  return new Promise((resolve, reject) => {
    const sock = net.connect({ host: '127.0.0.1', port: bridge.port })
    let buf = ''
    const timer = setTimeout(() => { sock.destroy(); reject(new Error('timeout')) }, 120000)
    sock.on('connect', () => {
      sock.write(JSON.stringify({ id: 1, method: 'hello', params: { token: bridge.token } }) + '\n')
      sock.write(JSON.stringify({ id: 2, method, params }) + '\n')
    })
    sock.on('data', (chunk) => {
      buf += chunk.toString('utf8')
      let i
      while ((i = buf.indexOf('\n')) >= 0) {
        const line = buf.slice(0, i)
        buf = buf.slice(i + 1)
        if (!line.trim()) continue
        let msg
        try { msg = JSON.parse(line) } catch { continue }
        if (msg.id === 1 && msg.ok === false) { clearTimeout(timer); sock.destroy(); reject(new Error('auth: ' + JSON.stringify(msg.error))) }
        if (msg.id === 2) {
          clearTimeout(timer)
          sock.destroy()
          if (msg.ok) resolve(msg.result)
          else reject(new Error(JSON.stringify(msg.error)))
        }
      }
    })
    sock.on('error', (e) => { clearTimeout(timer); reject(e) })
  })
}

const [sid, method, raw] = process.argv.slice(2)
if (!method) {
  console.error('usage: node test/rdp-rpc.mjs <sessionId|auto> <method> [json-params]')
  process.exit(2)
}
const bridge = pickBridge(sid)
const params = raw ? JSON.parse(raw) : {}
call(bridge, method, params).then((r) => {
  console.log(JSON.stringify(r, null, 2))
}).catch((e) => {
  console.error('ERROR: ' + e.message)
  process.exit(1)
})
