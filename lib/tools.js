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

// dsh validates tool output as lossless JSON: a property holding undefined
// makes the whole call fail ("value is not lossless JSON") even though
// JSON.stringify would silently drop it. Batch results are assembled from
// many optional pieces, so normalise the whole tree before returning it.
function plainJson(value) {
  if (value === undefined) return undefined
  if (value === null) return null
  if (typeof value === 'number') return Number.isFinite(value) ? value : null
  if (typeof value === 'bigint') return String(value)
  if (typeof value === 'function' || typeof value === 'symbol') return undefined
  if (value instanceof Date) return value.toISOString()
  if (Array.isArray(value)) {
    return value.map((v) => {
      const c = plainJson(v)
      return c === undefined ? null : c
    })
  }
  if (typeof value === 'object') {
    const out = {}
    for (const k of Object.keys(value)) {
      const c = plainJson(value[k])
      if (c !== undefined) out[k] = c
    }
    return out
  }
  return value
}

// Attach a captured PNG to the conversation (best effort: the attachment
// service is optional and the tool result still carries the file path).
async function attachShot(ctx, result, value) {
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
    description: 'Take a screenshot inside the RDP agent session. Returns the PNG path, width/height and origin (screen position of the image top-left; full-screen origin is usually 0,0). To click a pixel seen in a window/region image, use origin + image coordinates. Shows the image inline in the conversation.',
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
        savePath: { type: 'string', description: 'optional extra copy path, e.g. a workspace file. Relative paths go under the session user profile. The result reports savedTo, or saveError if the copy failed.' },
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
      if (a.savePath) params.savePath = a.savePath
      const result = await call('screenshot', params, 50000)
      const value = { ...result }
      await attachShot(ctx, result, value)
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
    name: 'rdp_state',
    description: 'One-call desktop snapshot and the PREFERRED first look at the RDP session. Returns: the target window (default foreground), a compact top-level window list, every actionable UI Automation element with a stable index i (type/name/value/rect/state), and an annotated screenshot whose red numbered boxes match those indices. Read the names and act with rdp_uia by index; use screenshot coordinates only when the UI exposes no elements (canvas apps). One rdp_state usually replaces screenshot + window list + element hunting.',
    parameters: {
      type: 'object',
      properties: {
        handle: { type: 'integer', description: 'Target window handle (default: foreground window).' },
        title: { type: 'string', description: 'Target window title substring.' },
        cls: { type: 'string', description: 'Target window class.' },
        process: { type: 'string', description: 'Target process name filter (recommended for windows you started).' },
        role: { type: 'string', description: 'Only elements of this control type, e.g. Button or Edit.' },
        interactive: { type: 'boolean', description: 'Only actionable controls (default true). false also includes Text/Image/containers for reading.' },
        max: { type: 'integer', description: 'Max elements to return (default 150, max 400).' },
        vision: { type: 'boolean', description: 'Include the annotated screenshot (default true). Set false for a text-only, token-cheap query.' },
        windows: { type: 'boolean', description: 'Include the top-level window list (default true).' },
        windowMax: { type: 'integer', description: 'Max windows in the list (default 25).' },
      },
    },
    output: { schema: outJson(), render: (args, value) => {
      const blocks = textBlock({ ...value, attachment: undefined })
      if (value.attachment) blocks.push({ type: 'image', attachment: value.attachment })
      return blocks
    } },
    timeoutMs: 90000,
    async execute(args) {
      const a = args || {}
      const params = {
        interactive: a.interactive !== false,
        max: Math.min(Math.max(Number(a.max) || 150, 1), 400),
        vision: a.vision !== false,
        windows: a.windows !== false,
        windowMax: Math.min(Math.max(Number(a.windowMax) || 25, 1), 100),
      }
      for (const k of ['handle', 'title', 'process', 'role']) if (a[k] != null) params[k] = a[k]
      if (a.cls != null) params.class = a.cls
      const result = await call('state', params, 80000)
      const value = { ...result }
      await attachShot(ctx, result, value)
      return value
    },
  })

  defs.push({
    name: 'rdp_uia',
    description: 'UI Automation actions inside the RDP session. Prefer these over pixel clicking. list = actionable elements with indices (refreshes the index cache); find = search by name/value substring and/or role (labels and text are included by default; appends matches and returns their indices); get = live properties of an indexed element (name/value/toggle/expanded/rect); click = invoke/toggle/select/expand by index (falls back to a real mouse click); setvalue = write text into an input (ValuePattern, or focus+keys/paste, submitting with Enter if asked); focus; wait = wait for an element by text (or gone=true for disappearance). Element indices come from the latest rdp_state / list; if the UI changed, take a new snapshot. If the target window exposes no automation (some Chromium/Electron/canvas UIs), fall back to rdp_screenshot + rdp_mouse.',
    parameters: {
      type: 'object',
      properties: {
        action: { type: 'string', enum: ['list', 'find', 'get', 'click', 'setvalue', 'focus', 'wait'], description: 'UIA action.' },
        i: { type: 'integer', description: 'Element index from rdp_state / rdp_uia list / find / wait.' },
        text: { type: 'string', description: 'find/click/setvalue/focus/wait: element name substring (case-insensitive; exact name preferred).' },
        role: { type: 'string', description: 'Optional control type filter: Button, Edit, MenuItem, ListItem, CheckBox, TabItem, ...' },
        value: { type: 'string', description: 'setvalue: text to put into the field (empty string clears it).' },
        submit: { type: 'boolean', description: 'setvalue: press Enter afterwards (default false).' },
        clear: { type: 'boolean', description: 'setvalue: select all before typing, replacing existing content.' },
        double: { type: 'boolean', description: 'click: double-click (real mouse double-click).' },
        interactive: { type: 'boolean', description: 'list: only actionable elements (default true). find: restrict to actionable elements (default false, so labels/text are searchable too).' },
        max: { type: 'integer', description: 'list/find: max elements (default 200 / 30).' },
        handle: { type: 'integer', description: 'Target window handle (default foreground).' },
        title: { type: 'string', description: 'Target window title substring.' },
        cls: { type: 'string', description: 'Target window class.' },
        process: { type: 'string', description: 'Target process name filter.' },
        timeoutMs: { type: 'integer', description: 'wait: timeout in ms (default 15000).' },
        gone: { type: 'boolean', description: 'wait: wait until the element disappears.' },
      },
      required: ['action'],
    },
    output: { schema: outJson(), render: (args, value) => textBlock(value) },
    timeoutMs: 90000,
    async execute(args) {
      const a = args || {}
      if (a.action === 'setvalue' && a.value == null) throw new Error('rdp_uia setvalue requires value')
      if ((a.action === 'click' || a.action === 'setvalue' || a.action === 'focus') && a.i == null && !a.text) {
        throw new Error('rdp_uia ' + a.action + ' requires i (element index) or text')
      }
      const params = { action: a.action }
      if (a.i != null) params.i = a.i
      if (a.text != null) params.text = String(a.text)
      if (a.role != null) params.role = String(a.role)
      if (a.value != null) params.value = String(a.value)
      if (a.submit) params.submit = true
      if (a.clear) params.clear = true
      if (a.double) params.double = true
      if (a.interactive != null) params.interactive = !!a.interactive
      if (a.max != null) params.max = a.max
      if (a.timeoutMs != null) params.timeoutMs = a.timeoutMs
      if (a.gone) params.gone = true
      for (const k of ['handle', 'title', 'process']) if (a[k] != null) params[k] = a[k]
      if (a.cls != null) params.class = a.cls
      return call('uia', params, 80000)
    },
  })

  defs.push({
    name: 'rdp_wait',
    description: 'Wait inside the RDP session until a condition is true instead of sleeping and re-screenshotting. mode=element: wait until a UI Automation element whose name contains text appears (gone=true: until it disappears). mode=window: wait for a window (handle/title/class/process) or its disappearance. mode=idle: wait until the screen region stops changing for stableMs (default 700) - use after clicks, menu opens and page loads so the next screenshot is current. mode=change: wait until the screen region changes from now - use after actions that trigger dialogs. Returns ok/found/waitedMs.',
    parameters: {
      type: 'object',
      properties: {
        mode: { type: 'string', enum: ['element', 'window', 'idle', 'change'], description: 'Condition to wait for.' },
        text: { type: 'string', description: 'element: name substring to wait for.' },
        role: { type: 'string', description: 'element: optional control type filter.' },
        gone: { type: 'boolean', description: 'element/window: wait until the element/window disappears.' },
        handle: { type: 'integer', description: 'window/element: target window handle.' },
        title: { type: 'string', description: 'window: title substring.' },
        cls: { type: 'string', description: 'window: class filter.' },
        process: { type: 'string', description: 'window: process name filter.' },
        timeoutMs: { type: 'integer', description: 'Timeout in ms (default 15000, max 180000).' },
        stableMs: { type: 'integer', description: 'idle: how long the screen must stay unchanged (default 700).' },
        x: { type: 'integer', description: 'idle/change: region left (default whole screen).' },
        y: { type: 'integer', description: 'idle/change: region top.' },
        width: { type: 'integer', description: 'idle/change: region width.' },
        height: { type: 'integer', description: 'idle/change: region height.' },
      },
      required: ['mode'],
    },
    output: { schema: outJson(), render: (args, value) => textBlock(value) },
    timeoutMs: 190000,
    async execute(args) {
      const a = args || {}
      const params = { mode: a.mode }
      for (const k of ['text', 'role', 'handle', 'title', 'process', 'timeoutMs', 'stableMs', 'x', 'y', 'width', 'height']) {
        if (a[k] != null) params[k] = a[k]
      }
      if (a.cls != null) params.class = a.cls
      if (a.gone) params.gone = true
      const t = Math.min(Math.max(Number(a.timeoutMs) || 15000, 100), 180000)
      return call('wait', params, t + 8000)
    },
  })

  defs.push({
    name: 'rdp_app',
    description: 'Launch a desktop app inside the RDP session and wait for its window in one call. Give a Start Menu display name (e.g. Notepad, Paint, Settings, Calculator), a common alias, or an exe path. Returns pid and the new window handle/title/rect as soon as it appears, so you can go straight to rdp_state with that handle. The result reports launchedName and match (exact/prefix/contains/exe) when a fuzzy Start Menu entry was used, so verify you got the intended app. Much better than rdp_exec wait=false plus window polling.',
    parameters: {
      type: 'object',
      properties: {
        name: { type: 'string', description: 'Start Menu display name or executable name on PATH.' },
        path: { type: 'string', description: 'Full executable path (alternative to name).' },
        args: { type: 'array', items: { type: 'string' }, description: 'Optional command-line arguments.' },
        title: { type: 'string', description: 'Optional window title substring to identify the new window.' },
        process: { type: 'string', description: 'Optional process name filter for the new window.' },
        timeoutMs: { type: 'integer', description: 'How long to wait for the window (default 20000).' },
      },
    },
    output: { schema: outJson(), render: (args, value) => textBlock(value) },
    timeoutMs: 60000,
    async execute(args) {
      const a = args || {}
      const params = {}
      if (a.name != null) params.name = String(a.name)
      if (a.path != null) params.path = String(a.path)
      if (Array.isArray(a.args)) params.args = a.args.map(String)
      if (a.title != null) params.title = String(a.title)
      if (a.process != null) params.process = String(a.process)
      if (a.timeoutMs != null) params.timeoutMs = a.timeoutMs
      const t = Math.min(Math.max(Number(a.timeoutMs) || 20000, 1000), 120000)
      return call('app', params, t + 15000)
    },
  })

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
      if (['move', 'click', 'dblclick', 'rightclick', 'middleclick', 'drag', 'scroll'].includes(a.action) &&
          (a.x == null || a.y == null)) {
        throw new Error('rdp_mouse ' + a.action + ' requires x and y (session pixels from rdp_screenshot/rdp_state)')
      }
      if (a.action === 'drag' && (a.x2 == null || a.y2 == null)) {
        throw new Error('rdp_mouse drag requires x2 and y2 (destination coordinates)')
      }
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
    description: 'Keyboard input inside the RDP session. Prefer rdp_uia setvalue when the target is an automation element; use this for apps with no elements, for shortcuts, or after you have focused a field. action=type sends Unicode text (a newline presses Enter); action=press sends a chord like ctrl+shift+esc, win+r, alt+F4, enter, tab. Non-ASCII text (e.g. Chinese) is delivered via-a clipboard-assisted paste (saved and restored); pure ASCII goes as real keystrokes. Click into the target input field first so keyboard focus is inside it.',
    parameters: {
      type: 'object',
      properties: {
        action: { type: 'string', enum: ['type', 'press'], description: 'type = Unicode text; press = key chord.' },
        text: { type: 'string', description: 'type: the text to type.' },
        keys: { type: 'string', description: 'press: chord, e.g. "ctrl+shift+esc", "win+r", "enter".' },
        clear: { type: 'boolean', description: 'type: press ctrl+a first, replacing existing field content.' },
      },
      required: ['action'],
    },
    output: { schema: outJson(), render: (args, value) => textBlock(value) },
    timeoutMs: 30000,
    async execute(args) {
      const a = args || {}
      if (a.action === 'type') {
        const params = { action: 'type', text: String(a.text ?? '') }
        if (a.clear) params.clear = true
        return call('key', params)
      }
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

  const byShort = new Map()
  for (const d of defs) byShort.set(d.name.replace(/^rdp_/, ''), d)

  defs.push({
    name: 'rdp_do',
    description: 'Run a batch of session steps in ONE tool call: steps is [{tool, args}] where tool is a session tool name with or without the rdp_ prefix (state, uia, wait, mouse, key, window, exec, app, clipboard, screenshot). Steps run in order; stopOnError defaults to true. Returns per-step results, errors and total elapsedMs. Use it for predictable sequences (click -> wait -> type -> Enter), then verify the final state once instead of round-tripping every action. Max 25 steps.',
    parameters: {
      type: 'object',
      properties: {
        steps: {
          type: 'array',
          description: 'Ordered steps to execute.',
          items: {
            type: 'object',
            properties: {
              tool: { type: 'string', description: 'Session tool name, e.g. uia, wait, key, mouse, state.' },
              args: { type: 'object', description: 'Arguments for that tool, same shape as calling it directly.' },
            },
            required: ['tool'],
          },
        },
        stopOnError: { type: 'boolean', description: 'Stop at the first failing step (default true).' },
      },
      required: ['steps'],
    },
    output: { schema: outJson(), render: (args, value) => {
      const blocks = textBlock({ ...value, attachments: undefined })
      for (const att of value.attachments || []) blocks.push({ type: 'image', attachment: att })
      return blocks
    } },
    timeoutMs: 300000,
    async execute(args, exec) {
      const a = args || {}
      const steps = Array.isArray(a.steps) ? a.steps : []
      if (!steps.length) throw new Error('rdp_do requires a non-empty steps array')
      if (steps.length > 25) throw new Error('rdp_do supports at most 25 steps')
      const stopOnError = a.stopOnError !== false
      const t0 = Date.now()
      const results = []
      const images = []
      for (const step of steps) {
        const tool = String((step && step.tool) || '').replace(/^rdp_/, '')
        const def = byShort.get(tool)
        if (!def || def.name === 'rdp_do' || def.name === 'rdp_info') {
          results.push({ tool, ok: false, error: 'unsupported tool in batch: ' + tool })
          if (stopOnError) break
          continue
        }
        try {
          const r = await def.execute((step && step.args) || {}, exec)
          const copy = { ...(r || {}) }
          if (copy.attachment) {
            images.push(copy.attachment)
            delete copy.attachment
          }
          results.push({ tool, ok: true, result: copy })
        } catch (e) {
          results.push({ tool, ok: false, error: errText(e) })
          if (stopOnError) break
        }
      }
      const completed = results.filter((r) => r.ok).length
      const out = {
        ok: completed === results.length,
        completed,
        total: steps.length,
        elapsedMs: Date.now() - t0,
        steps: results,
      }
      if (images.length) out.attachments = images
      return plainJson(out)
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
