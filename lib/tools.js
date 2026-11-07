// dsh-rdp-wrapper : native dsh tool registration
import path from 'node:path'
import fs from 'node:fs'
import { fileURLToPath } from 'node:url'
import { spawn } from 'node:child_process'
import { callBridge, findBridge, listBridges, readConfig, RDP_ROOT } from './bridge.js'
import { trace } from './trace.js'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
export const PLUGIN_ROOT = path.resolve(__dirname, '..')
const SCRIPTS = path.join(PLUGIN_ROOT, 'scripts')
const BS = String.fromCharCode(92)

// Run one of the plugin's PowerShell scripts; resolve with {code, stdout, stderr}.
export function runPs(scriptName, args = [], timeoutMs = 120000) {
  return new Promise((resolve) => {
    const file = path.join(SCRIPTS, scriptName)
    const full = ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', file, ...args]
    let child
    try {
      child = spawn('powershell.exe', full, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] })
    } catch (e) {
      return resolve({ code: -1, stdout: '', stderr: String(e && e.message || e) })
    }
    let stdout = ''
    let stderr = ''
    let done = false
    const timer = setTimeout(() => {
      if (!done) { done = true; try { child.kill() } catch { } ; resolve({ code: -1, stdout, stderr: (stderr + String.fromCharCode(10) + 'script timed out after ' + timeoutMs + 'ms').trim() }) }
    }, timeoutMs)
    child.stdout.on('data', (c) => { stdout += c.toString('utf8') })
    child.stderr.on('data', (c) => { stderr += c.toString('utf8') })
    child.on('error', (e) => { if (!done) { done = true; clearTimeout(timer); resolve({ code: -1, stdout, stderr: String(e && e.message || e) }) } })
    child.on('close', (code) => { if (!done) { done = true; clearTimeout(timer); resolve({ code: code == null ? -1 : code, stdout, stderr }) } })
  })
}

function errText(e) {
  return String((e && e.message) || e)
}

// Render helper: model sees the execute() value; UI sees these blocks.
function textBlock(value) {
  return [{ type: 'text', text: JSON.stringify(value, null, 2) }]
}

function outJson() {
  return { type: 'object' }
}

// Simple registration wrapper; keeps a disposer list. Resolves the tools
// service lazily so the plugin still loads when it is absent.
function reg(ctx, def, disposers) {
  const fail = (msg) => {
    trace('register ' + def.name + ' FAILED: ' + msg)
    try { (ctx.logger?.error || console.error)('[dsh-rdp-wrapper] register ' + def.name + ' failed: ' + msg) } catch { }
    return false
  }
  try {
    trace('reg ' + def.name + ': resolving tools service (ctx.get=' + typeof ctx.get + ')')
    // ctx.tools is the caller-bound service (inject declares it) and is the
    // idiomatic form; ctx.get() is the fallback for environments without the
    // bound proxy.
    let tools = null
    try { tools = ctx.tools } catch (e) { trace('reg ' + def.name + ': ctx.tools threw: ' + errText(e)) }
    if (!tools) { try { tools = ctx.get && ctx.get('tools') } catch (e) { trace('reg ' + def.name + ': ctx.get(tools) threw: ' + errText(e)) } }
    trace('reg ' + def.name + ': service=' + (tools ? typeof tools.register : 'none'))
    if (!tools || typeof tools.register !== 'function') {
      return fail('tools service unavailable (get=' + typeof ctx.get + ')')
    }
    trace('reg ' + def.name + ': calling register')
    const d = tools.register(def)
    trace('reg ' + def.name + ': register returned ' + typeof d)
    disposers.push(typeof d === 'function' ? d : null)
    return true
  } catch (e) {
    return fail(errText(e) + ' | stack: ' + String((e && e.stack) || '').split(String.fromCharCode(10)).slice(0, 3).join(' / '))
  }
}

// ---------------------------------------------------------------------------
// Tool definitions
// ---------------------------------------------------------------------------

export function buildToolDefs(ctx) {
  const log = (msg) => { try { (ctx.logger?.info || console.log)('[dsh-rdp-wrapper] ' + msg) } catch { } }
  const defs = []

  defs.push({
    name: 'rdp_doctor',
    description: 'Read-only health check of the local RDP host for the dsh-rdp-wrapper sandbox: OS/termsrv version, port 3389, fDenyTSConnections policy, rdpwrap state, child-session support (WTSEnableChildSessions), child host files, quser sessions and live bridges. Run this first; change nothing.',
    parameters: { type: 'object', properties: {} },
    output: { schema: outJson(), render: (args, value) => textBlock(value) },
    timeoutMs: 120000,
    async execute() {
      const r = await runPs('rdp-doctor.ps1', [], 100000)
      return { ok: r.code === 0, exitCode: r.code, report: (r.stdout || r.stderr || '').trim() }
    },
  })

  defs.push({
    name: 'rdp_provision',
    description: 'One-time elevated setup for the RDP sandbox (child-session model): create data directories and ACLs, copy the in-session bridge into ProgramData, write the config and ensure RDP connections are allowed. No dedicated agent account or logon task is created: child sessions clone the current interactive logon. Requires UAC elevation (silent on hosts configured for it).',
    parameters: {
      type: 'object',
      properties: {
        agentUser: { type: 'string', description: 'Account that owns the sandbox files. Default: the current console user.' },
      },
    },
    output: { schema: outJson(), render: (args, value) => textBlock(value) },
    timeoutMs: 300000,
    async execute(args) {
      const a = ['provision']
      if (args && args.agentUser) a.push('-AgentUser', String(args.agentUser))
      const r = await runPs('rdp-elevate.ps1', a, 280000)
      return { ok: r.code === 0, exitCode: r.code, output: (r.stdout || '').trim(), errors: (r.stderr || '').trim() }
    },
  })

  defs.push({
    name: 'rdp_connect',
    description: 'Connect the agent into an isolated RDP child session on this machine (idempotent). The host creates the child session from the current interactive logon via MsRdpClient10 ConnectToChildSession (no password), starts the in-session bridge, and keeps the session alive. Returns the session and bridge details.',
    parameters: {
      type: 'object',
      properties: {
        waitSec: { type: 'integer', description: 'Seconds to wait for the session and bridge. Default 60.' },
        width: { type: 'integer', description: 'Session desktop width in pixels. Default from config (1920).' },
        height: { type: 'integer', description: 'Session desktop height in pixels. Default from config (1080).' },
      },
    },
    output: { schema: outJson(), render: (args, value) => textBlock(value) },
    timeoutMs: 240000,
    async execute(args) {
      const a = ['-WaitSec', String((args && args.waitSec) || 60)]
      if (args && args.width) a.push('-Width', String(args.width))
      if (args && args.height) a.push('-Height', String(args.height))
      const r = await runPs('rdp-connect.ps1', a, 220000)
      return { ok: r.code === 0, exitCode: r.code, output: (r.stdout || '').trim(), errors: (r.stderr || '').trim() }
    },
  })

  defs.push({
    name: 'rdp_status',
    description: 'Current status of the RDP sandbox: child host state, interactive sessions (quser), live bridges (port/token files). Read-only.',
    parameters: { type: 'object', properties: {} },
    output: { schema: outJson(), render: (args, value) => textBlock(value) },
    timeoutMs: 90000,
    async execute() {
      const r = await runPs('rdp-status.ps1', [], 80000)
      return { ok: r.code === 0, exitCode: r.code, report: (r.stdout || r.stderr || '').trim() }
    },
  })

  defs.push({
    name: 'rdp_disconnect',
    description: 'End the RDP sandbox: stop the child host, log off the child session, and remove stale bridge files. Any in-session programs the agent started are terminated; the console session is never touched.',
    parameters: { type: 'object', properties: {} },
    output: { schema: outJson(), render: (args, value) => textBlock(value) },
    timeoutMs: 120000,
    async execute() {
      const r = await runPs('rdp-disconnect.ps1', [], 110000)
      return { ok: r.code === 0, exitCode: r.code, output: (r.stdout || r.stderr || '').trim() }
    },
  })

  return defs
}

// Session tools (talk to the in-session bridge over 127.0.0.1)
export function buildSessionToolDefs(ctx) {
  const defs = []
  const needBridge = async () => {
    const b = findBridge()
    if (!b) throw new Error('no agent-session bridge found. Run rdp_connect first (and check rdp_status).')
    return b
  }
  const call = async (method, params, timeoutMs) => {
    const bridge = await needBridge()
    const result = await callBridge(method, params, { bridge, timeoutMs: timeoutMs || 45000 })
    return { bridge: { session: bridge.session, user: bridge.user, port: bridge.port }, ...result }
  }

  defs.push({
    name: 'rdp_info',
    description: 'Identity and capability info of the RDP sandbox session: session id, desktop size, logged-on user, bridge uptime. Use to confirm which session you are operating on.',
    parameters: { type: 'object', properties: {} },
    output: { schema: outJson(), render: (args, value) => textBlock(value) },
    timeoutMs: 30000,
    async execute() { return call('info') },
  })

  defs.push({
    name: 'rdp_screenshot',
    description: 'Take a screenshot inside the RDP agent session. Returns image dimensions (coordinates for rdp_mouse are session physical pixels matching this image), saves the PNG under C:/ProgramData/dsh-rdp-wrapper/data/screenshots and shows it inline in the conversation.',
    parameters: {
      type: 'object',
      properties: {
        mode: { type: 'string', enum: ['full', 'region', 'window'], description: 'full (default) = whole virtual screen; region needs x/y/width/height; window needs handle or title/class/process filter.' },
        x: { type: 'integer', description: 'region mode: left edge.' },
        y: { type: 'integer', description: 'region mode: top edge.' },
        width: { type: 'integer', description: 'region mode: width.' },
        height: { type: 'integer', description: 'region mode: height.' },
        handle: { type: 'integer', description: 'window mode: window handle (from rdp_window list).' },
        title: { type: 'string', description: 'window mode: title substring.' },
        cls: { type: 'string', description: 'window mode: window class.' },
        process: { type: 'string', description: 'window mode: process name filter, e.g. notepad (recommended to target windows you started).' },
        savePath: { type: 'string', description: 'optional extra copy path.' },
      },
    },
    output: { schema: outJson(), render: (args, value) => textBlock(value) },
    timeoutMs: 60000,
    async execute(args, exec) {
      const a = args || {}
      const params = { mode: a.mode || 'full' }
      if (params.mode === 'region') {
        params.x = a.x; params.y = a.y; params.w = a.width; params.h = a.height
      } else if (params.mode === 'window') {
        params.handle = a.handle; params.title = a.title; params.class = a.cls; params.process = a.process
      }
      const result = await call('screenshot', params, 50000)
      const value = { ...result }
      // Inline image in the conversation when the attachment service is available.
      try {
        if (result.file && fs.existsSync(result.file)) {
          const bytes = fs.readFileSync(result.file)
          const attachments = ctx && ctx.get ? ctx.get('attachments') : null
          if (attachments && typeof attachments.saveImage === 'function') {
            const att = await attachments.saveImage({
              data: new Uint8Array(bytes),
              mediaType: 'image/png',
              name: path.basename(result.file),
            })
            value.attachment = att
          }
        }
      } catch (e) {
        value.attachmentWarning = 'inline image failed: ' + errText(e)
      }
      return value
    },
  })
  // The screenshot render shows the image itself when an attachment exists.
  const shot = defs[defs.length - 1]
  shot.output.render = (args, value) => {
    const blocks = textBlock({ ...value, attachment: undefined })
    if (value.attachment) blocks.push({ type: 'image', attachment: value.attachment })
    return blocks
  }


  defs.push({
    name: 'rdp_mouse',
    description: 'Mouse actions inside the RDP agent session: move / click / dblclick / rightclick / middleclick / drag / scroll. Coordinates are session physical pixels as seen in rdp_screenshot. mode=auto (default) and mode=input use real SendInput injection; mode=message posts window messages to the control under the pointer (useful for background or occluded standard dialogs without moving the cursor; self-drawn surfaces such as the desktop and taskbar ignore message clicks).',
    parameters: {
      type: 'object',
      properties: {
        action: { type: 'string', enum: ['move', 'click', 'dblclick', 'rightclick', 'middleclick', 'drag', 'scroll'], description: 'Mouse action.' },
        x: { type: 'integer', description: 'X in session pixels (all actions).' },
        y: { type: 'integer', description: 'Y in session pixels (all actions).' },
        x2: { type: 'integer', description: 'drag: destination X.' },
        y2: { type: 'integer', description: 'drag: destination Y.' },
        delta: { type: 'integer', description: 'scroll: positive scrolls up, negative scrolls down (notches, default 3).' },
        mode: { type: 'string', enum: ['auto', 'input', 'message'], description: 'auto (default) and input: real SendInput injection; message: window messages only.' },
        waitPopup: { type: 'boolean', description: 'rightclick only (default true): wait until the context menu is rendered before returning, so a following rdp_screenshot shows it. XAML menus need a few seconds on first use (up to ~6s); classic Win32 menus return immediately. Set false to return right after the click (a right-click that opens no menu then avoids the wait).' },
      },
      required: ['action'],
    },
    output: { schema: outJson(), render: (args, value) => textBlock(value) },
    timeoutMs: 30000,
    async execute(args) {
      const a = args || {}
      const params = { action: a.action, x: a.x, y: a.y }
      if (a.mode) params.mode = a.mode
      if (a.action === 'rightclick' && a.waitPopup != null) params.waitPopup = !!a.waitPopup
      if (a.action === 'drag') { params.x2 = a.x2; params.y2 = a.y2 }
      if (a.action === 'scroll') params.amount = a.delta != null ? a.delta : (a.amount != null ? a.amount : 3)
      return call('mouse', params)
    },
  })

  defs.push({
    name: 'rdp_key',
    description: 'Keyboard input inside the RDP agent session. action=type sends Unicode text (a newline in the text presses Enter); action=press sends a chord like ctrl+shift+esc, win+r, alt+F4, enter, tab. Text containing any non-ASCII character (e.g. Chinese) is delivered via a clipboard-assisted paste: the session clipboard is saved, set to the text, pasted with ctrl+v and best-effort restored. Pure ASCII is sent as real keystrokes. Click into the target input field first so keyboard focus is inside it.',
    parameters: {
      type: 'object',
      properties: {
        action: { type: 'string', enum: ['type', 'press'], description: 'type = Unicode text; press = key chord.' },
        text: { type: 'string', description: 'type: the text to type.' },
        keys: { type: 'string', description: 'press: chord, e.g. "ctrl+shift+esc", "win+r", "enter".' },
      },
      required: ['action'],
    },
    output: { schema: outJson(), render: (args, value) => textBlock(value) },
    timeoutMs: 30000,
    async execute(args) {
      const a = args || {}
      if (a.action === 'type') return call('key', { action: 'type', text: String(a.text ?? '') })
      return call('key', { action: 'press', keys: String(a.keys || '') })
    },
  })

  defs.push({
    name: 'rdp_window',
    description: 'Window management inside the RDP agent session: list / focus / minimize / maximize / restore / close / move / resize / rect / wait. Filter windows by handle, title substring, class or process name. Prefer the process filter for windows you started yourself to avoid touching windows of other users.',
    parameters: {
      type: 'object',
      properties: {
        action: { type: 'string', enum: ['list', 'focus', 'minimize', 'maximize', 'restore', 'close', 'move', 'resize', 'rect', 'wait'], description: 'Window action.' },
        handle: { type: 'integer', description: 'Target window handle (from list).' },
        title: { type: 'string', description: 'Title substring filter.' },
        cls: { type: 'string', description: 'Window class filter.' },
        process: { type: 'string', description: 'Process name filter (recommended).' },
        x: { type: 'integer', description: 'move: new left.' },
        y: { type: 'integer', description: 'move: new top.' },
        width: { type: 'integer', description: 'resize: new width.' },
        height: { type: 'integer', description: 'resize: new height.' },
        timeoutMs: { type: 'integer', description: 'wait: how long to wait for the window.' },
      },
      required: ['action'],
    },
    output: { schema: outJson(), render: (args, value) => textBlock(value) },
    timeoutMs: 45000,
    async execute(args) {
      const a = args || {}
      const params = { action: a.action }
      for (const k of ['handle', 'title', 'class', 'process', 'x', 'y', 'w', 'h', 'timeoutMs']) {
        let v = a[k]
        if (k === 'w' && v == null) v = a.width
        if (k === 'h' && v == null) v = a.height
        if (k === 'class' && v == null) v = a.cls
        if (v != null) params[k] = v
      }
      return call('window', params, 40000)
    },
  })

  defs.push({
    name: 'rdp_exec',
    description: 'Run a command INSIDE the RDP child session (has a desktop and the logon profile, unlike harness-side exec). shell=true wraps with cmd /d /c and returns UTF-8 stdout/stderr/exitCode; GUI programs can use wait=false to start and return the pid immediately.',
    parameters: {
      type: 'object',
      properties: {
        command: { type: 'string', description: 'Command line to run.' },
        shell: { type: 'boolean', description: 'Wrap in cmd /d /c (default true).' },
        wait: { type: 'boolean', description: 'Wait for exit (default true). Use false for GUI apps to get the pid.' },
        timeoutMs: { type: 'integer', description: 'Execution timeout, max 600000.' },
      },
      required: ['command'],
    },
    output: { schema: outJson(), render: (args, value) => textBlock(value) },
    timeoutMs: 620000,
    async execute(args) {
      const a = args || {}
      const t = Math.min(Math.max(Number(a.timeoutMs) || 600000, 1000), 600000)
      return call('exec', { command: String(a.command), shell: a.shell !== false, wait: a.wait !== false, timeoutMs: t }, t + 15000)
    },
  })

  defs.push({
    name: 'rdp_clipboard',
    description: 'Read or write the RDP agent session clipboard text (get / set).',
    parameters: {
      type: 'object',
      properties: {
        action: { type: 'string', enum: ['get', 'set'], description: 'get = read clipboard; set = write clipboard.' },
        text: { type: 'string', description: 'set: text to place on the clipboard.' },
      },
      required: ['action'],
    },
    output: { schema: outJson(), render: (args, value) => textBlock(value) },
    timeoutMs: 30000,
    async execute(args) {
      const a = args || {}
      return call('clipboard', { action: a.action, text: a.text != null ? String(a.text) : undefined })
    },
  })

  return defs
}

// Register every tool; returns the count of successfully registered tools.
export function registerTools(ctx, disposers = []) {
  let n = 0
  trace('registerTools: building defs')
  const all = [...buildToolDefs(ctx), ...buildSessionToolDefs(ctx)]
  trace('registerTools: defs built ' + all.length)
  for (const def of all) {
    trace('registerTools: reg ' + def.name)
    if (reg(ctx, def, disposers)) n++
  }
  return n
}
