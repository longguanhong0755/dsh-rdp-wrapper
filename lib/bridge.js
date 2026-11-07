// dsh-rdp-wrapper : bridge TCP client (one request per connection)
import net from 'node:net'
import fs from 'node:fs'
import path from 'node:path'

const PD = process.env['ProgramData'] || ['C:', 'ProgramData'].join(String.fromCharCode(92))
export const RDP_ROOT = path.join(PD, 'dsh-rdp-wrapper')

export function readConfig() {
  try { return JSON.parse(fs.readFileSync(path.join(RDP_ROOT, 'config.json'), 'utf8')) } catch { return null }
}

// Read the child-host status file (written by rdp-child-host.exe).
export function readChildHost() {
  try { return JSON.parse(fs.readFileSync(path.join(RDP_ROOT, 'ports', 'childhost.json'), 'utf8')) } catch { return null }
}

// Collect bridge candidates from the ports directory, newest first.
// opts: { session, user, anyUser } filters. With no filters the bridge that
// belongs to the live child session (childhost.json) is preferred; the child
// session is a clone of the interactive logon, so its user is the console
// user, not the (legacy) agentUser account.
export function collectBridges(opts = {}) {
  let entries = []
  try { entries = fs.readdirSync(path.join(RDP_ROOT, 'ports')) } catch { return [] }
  let bridges = []
  for (const f of entries.filter((f) => /^bridge-session-\d+\.json$/.test(f))) {
    try {
      const j = JSON.parse(fs.readFileSync(path.join(RDP_ROOT, 'ports', f), 'utf8'))
      j._file = f
      bridges.push(j)
    } catch { }
  }
  if (opts.session != null) bridges = bridges.filter((b) => b.session === opts.session)
  if (opts.user) {
    const u = String(opts.user).toLowerCase()
    bridges = bridges.filter((b) => {
      const bu = String(b.user || '').toLowerCase()
      return bu === u || bu.endsWith(String.fromCharCode(92) + u)
    })
  }
  bridges.sort((a, b) => String(b.started || '').localeCompare(String(a.started || '')))
  if (!opts.session && !opts.user && !opts.anyUser) {
    const host = readChildHost()
    if (host && host.childSessionId) {
      const own = bridges.filter((b) => b.session === host.childSessionId)
      if (own.length) bridges = own
    }
  }
  return bridges
}

export function findBridge(opts = {}) {
  return collectBridges(opts)[0] || null
}

export function listBridges() {
  return collectBridges({ anyUser: true })
}

// One connection, one request: hello(token) -> request -> response -> close.
// Stale port files are tolerated: connection-level failures fall through to
// the next candidate bridge.
export function callBridge(method, params = {}, opts = {}) {
  const targets = opts.bridge ? [opts.bridge] : collectBridges(opts)
  if (targets.length === 0) {
    const err = new Error('no bridge found (is the RDP session connected? run rdp_connect)')
    err.code = 'ENOBRIDGE'
    throw err
  }
  const timeoutMs = opts.timeoutMs || 45000
  const attempt = (i) => callOne(targets[i], method, params, timeoutMs).catch((e) => {
    const transient = e && (e.code === 'ECONNREFUSED' || e.code === 'EHOSTUNREACH' ||
      e.code === 'ECONNRESET' || e.code === 'ETIMEDOUT' || e.code === 'EPIPE')
    if (transient && i + 1 < targets.length) return attempt(i + 1)
    throw e
  })
  return attempt(0)
}

function callOne(target, method, params, timeoutMs) {
  return new Promise((resolve, reject) => {
    const sock = net.connect({ host: '127.0.0.1', port: target.port })
    let buf = ''
    const finish = (fn, arg) => {
      try { sock.destroy() } catch { }
      fn(arg)
    }
    const timer = setTimeout(() => finish(reject, new Error('bridge call timed out after ' + timeoutMs + 'ms (' + method + ')')), timeoutMs)
    sock.on('connect', () => {
      sock.write(JSON.stringify({ id: 1, method: 'hello', params: { token: target.token } }) + '\n')
      sock.write(JSON.stringify({ id: 2, method, params }) + '\n')
    })
    sock.on('data', (chunk) => {
      buf += chunk.toString('utf8')
      let idx
      while ((idx = buf.indexOf(String.fromCharCode(10))) >= 0) {
        const line = buf.slice(0, idx)
        buf = buf.slice(idx + 1)
        if (!line.trim()) continue
        let msg
        try { msg = JSON.parse(line) } catch { continue }
        if (msg.id === 1) {
          if (msg.ok === false) return finish(reject, new Error('bridge auth failed: ' + (msg.error && msg.error.message)))
          continue
        }
        if (msg.id === 2) {
          clearTimeout(timer)
          if (msg.ok) return finish(resolve, msg.result)
          const e = new Error((msg.error && msg.error.message) || 'bridge error')
          e.code = 'BRIDGEERR'
          return finish(reject, e)
        }
      }
    })
    sock.on('error', (e) => { clearTimeout(timer); finish(reject, e) })
  })
}
