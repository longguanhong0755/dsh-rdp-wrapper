// dsh-rdp-wrapper : DeepSeek Harness (dsh) plugin entry
// Gives the agent a real RDP loopback desktop session with computer-use
// primitives: screenshot / mouse / keyboard / windows / in-session exec / clipboard.
import { registerTools } from './tools.js'
import { trace } from './trace.js'

export const name = 'dsh-rdp-wrapper'
// `tools` must be injected, not merely looked up: a bundle entry can be applied
// before the tools service exists, and cordis only defers apply() for declared
// injects. Everything else (attachments, logger, ...) stays lazily resolved.
export const inject = ['tools']

const NL = String.fromCharCode(10)

const GUIDE = [
  '## RDP desktop sandbox (dsh-rdp-wrapper)',
  '',
  'You have tools that put you inside a real interactive Windows desktop session',
  '(an isolated RDP child session cloned from the current console logon; the',
  'console session you are running in is never touched): rdp_doctor, rdp_provision,',
  'rdp_connect, rdp_status, rdp_disconnect, rdp_info, rdp_state, rdp_screenshot,',
  'rdp_uia, rdp_wait, rdp_do, rdp_app, rdp_mouse, rdp_key, rdp_window, rdp_exec,',
  'rdp_clipboard.',
  '',
  'Workflow: rdp_doctor (read-only check) -> rdp_connect (idempotent) -> rdp_state ->',
  'operate -> rdp_disconnect when done. rdp_provision is a one-time elevated setup;',
  'rdp_doctor tells you if it is needed.',
  '',
  'Work efficiently:',
  '- Start most tasks with rdp_state: one call returns the foreground window, all',
  '  top-level windows, every actionable UI Automation element with an index i, and',
  '  a screenshot whose numbered boxes match those indices. Act on elements by index.',
  '- rdp_uia click {i} invokes/toggles/selects the element directly; no coordinate',
  '  guessing. rdp_uia setvalue {i, value} fills an input reliably, including Chinese',
  '  text (submit=true presses Enter). rdp_uia find {text} locates an element when you',
  '  do not know its index; rdp_uia get {i} reads live text/value/state.',
  '- Element indices are valid for the latest rdp_state / rdp_uia list. find and wait',
  '  append to the cache; if an action reports a stale element, take a new rdp_state.',
  '- After actions that change the UI, prefer rdp_wait (mode=element|window|idle) over',
  '  sleeping. mode=idle waits until the screen is stable, so the next look is current.',
  '- Batch predictable sequences with rdp_do (click -> wait -> type -> Enter) and verify',
  '  only the outcome instead of re-screenshotting after every step.',
  '- Use rdp_app {name} to launch an app and get its window in one call.',
  '- rdp_screenshot is for canvas UIs (Paint), visual checks, and apps that expose no',
  '  automation (some Chromium/Electron windows). Use rdp_mouse on the same coordinates',
  '  and re-screenshot after window changes.',
  '',
  'Semantics:',
  '- Coordinates in rdp_mouse are session physical pixels. Full-screen screenshots',
  '  match them 1:1; for a window/region screenshot add the returned origin to the',
  '  pixel you measured in that image.',
  '- rdp_exec runs INSIDE the session (its own desktop and user profile).',
  '- When filtering windows, prefer the process filter for windows YOU started, to',
  '  avoid touching windows that belong to other users.',
  '- rdp_mouse uses real SendInput by default (mode=auto). If a standard dialog is',
  '  occluded/background and you do not want to move the pointer, set',
  '  mode=message; self-drawn surfaces (desktop, taskbar) ignore message clicks.',
].join(NL)

function errText(e) {
  return String((e && e.message) || e)
}

// cordis throws when an undeclared service property is accessed on ctx, so
// every service and logger access must be guarded like this.
function safeService(ctx, key) {
  try {
    const svc = ctx.get && ctx.get(key)
    if (svc) return svc
  } catch (e) {
    trace('ctx.get(' + key + ') threw: ' + errText(e))
  }
  return null
}

function log(ctx, level, msg) {
  try {
    const lg = safeService(ctx, 'logger')
    if (lg && typeof lg[level] === 'function') {
      lg[level]('[dsh-rdp-wrapper] ' + msg)
      return
    }
  } catch { }
  try { (console[level] || console.log)('[dsh-rdp-wrapper] ' + msg) } catch { }
}

export function apply(ctx, rawConfig) {
  trace('apply() entered')
  const disposers = []

  // System prompt guidance (best effort; never fail plugin load over it).
  trace('resolving systemPrompt')
  const systemPrompt = safeService(ctx, 'systemPrompt')
  trace('systemPrompt=' + (systemPrompt ? typeof systemPrompt.section : 'none'))
  try {
    if (systemPrompt && typeof systemPrompt.section === 'function') {
      const d = systemPrompt.section({
        name: 'tool:rdp-sandbox',
        order: 130,
        text: GUIDE,
      })
      if (typeof d === 'function') disposers.push(d)
      trace('systemPrompt section added')
    }
  } catch (e) {
    trace('systemPrompt section failed: ' + errText(e))
    log(ctx, 'warn', 'systemPrompt section skipped: ' + errText(e))
  }

  // Tool registration must never throw out of apply(): a failed registration
  // should leave the plugin loaded (and diagnosable), not fail the fiber.
  let registered = 0
  try {
    registered = registerTools(ctx, disposers)
  } catch (e) {
    trace('registerTools threw: ' + String(e && e.stack || e))
  }
  trace('registerTools done: ' + registered + ' tool(s)')
  log(ctx, registered > 0 ? 'info' : 'warn', 'loaded: ' + registered + ' tool(s) registered')

  return () => {
    for (const d of disposers) { try { d && d() } catch { } }
  }
}
