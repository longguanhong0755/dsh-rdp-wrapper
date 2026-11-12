// End-to-end regression for the 0.5.0 tool surface. Drives tool execute()
// functions exactly like the dsh agent does (same code path), against a real
// RDP child session. Requires: rdp_connect already done and the bridge
// deployed. ASCII-only assertions except deliberate CJK payloads.
import fs from 'node:fs'
import { buildToolDefs, buildSessionToolDefs } from '../lib/tools.js'

const ctx = { get: () => null, logger: console }
const defs = [...buildToolDefs(ctx), ...buildSessionToolDefs(ctx)]
const byName = new Map(defs.map((d) => [d.name, d]))

let pass = 0
let fail = 0
const failures = []
function check(name, cond, detail) {
  if (cond) { pass++; console.log('PASS ' + name) }
  else {
    fail++
    failures.push(name + (detail === undefined ? '' : ' :: ' + JSON.stringify(detail).slice(0, 400)))
    console.log('FAIL ' + name + (detail === undefined ? '' : ' :: ' + JSON.stringify(detail).slice(0, 400)))
  }
}
async function run(tool, args) {
  const d = byName.get(tool)
  if (!d) throw new Error('no tool ' + tool)
  return d.execute(args || {}, {})
}
async function expectError(name, tool, args, needle) {
  try {
    const r = await run(tool, args)
    check(name, false, { unexpected: r })
  } catch (e) {
    const msg = String((e && e.message) || e)
    check(name, msg.includes(needle), msg)
  }
}
function findEl(res, name, type) {
  const els = (res && res.elements) || []
  return els.find((e) => (e.name || '') === name && (!type || e.type === type))
}

const stamp = 'E2E-' + Date.now().toString(36)
const uiExe = 'D:/workspace/dsh-rdp-wrapper/test/ui/RdpTestUi.exe'

console.log('== app launch ==')
const app = await run('rdp_app', { path: uiExe, process: 'RdpTestUi', timeoutMs: 20000 })
check('app: found test window', app.found === true && (app.window || {}).process === 'RdpTestUi', app)
check('app: match=path', app.match === 'path', app.match)
const hwnd = (app.window || {}).handle

console.log('== state snapshot ==')
const st = await run('rdp_state', { process: 'RdpTestUi', vision: true, windows: false, max: 80 })
check('state: returns elements', Array.isArray(st.elements) && st.elements.length >= 12, st.total)
check('state: Document included', !!findEl(st, 'Notes input', 'Document'), st.elements && st.elements.length)
check('state: Edit included', !!findEl(st, 'Name input', 'Edit'))
check('state: button present', !!findEl(st, 'Say Hello', 'Button'))
check('state: no bogus toggle on Edit', !findEl(st, 'Name input', 'Edit').toggle, findEl(st, 'Name input', 'Edit'))
check('state: checkbox has toggle=off', (findEl(st, 'Enable option', 'CheckBox') || {}).toggle === 'off')
check('state: annotated png exists', !!st.file && fs.existsSync(st.file) && st.bytes > 1000, { file: st.file, bytes: st.bytes })
check('state: no scrollbar buttons', !st.elements.some((e) => String(e.name || '').indexOf('后滚') >= 0), st.total)
const docEl = findEl(st, 'Notes input', 'Document')
check('state: document index present', !!docEl && Number.isInteger(docEl.i), docEl)

console.log('== uia get on Document ==')
const gd = await run('rdp_uia', { action: 'get', i: docEl.i })
check('get: document readable', gd.type === 'Document' && gd.name === 'Notes input', gd)

console.log('== setvalue by text (CJK via value/paste) ==')
const sv = await run('rdp_uia', { action: 'setvalue', text: 'Name input', value: 'E2E 中文 123', clear: true })
check('setvalue: name via keyboard/paste', sv.set === true && ['keys', 'paste', 'value'].includes(sv.via), sv)
const gv = await run('rdp_uia', { action: 'find', text: 'E2E 中文 123' })
check('setvalue: value readable via find', !!findEl(gv, 'Name input', 'Edit'), gv)

const sd = await run('rdp_uia', { action: 'setvalue', text: 'Notes input', value: 'line1 中文\nline2 ' + stamp })
check('setvalue: document', sd.set === true, sd)
const gd2 = await run('rdp_uia', { action: 'get', i: sd.i })
check('get: document text contains typed value', typeof gd2.text === 'string' && gd2.text.indexOf('line1 中文') >= 0, gd2)

console.log('== click by text + read result ==')
const cl = await run('rdp_uia', { action: 'click', text: 'Say Hello' })
check('click: by text invokes button', cl.clicked === true && cl.via === 'invoke', cl)
const lbl = await run('rdp_uia', { action: 'find', text: 'Hello, E2E 中文 123' })
check('click: output label updated', !!findEl(lbl, 'Hello, E2E 中文 123 / ', 'Text') || (lbl.elements || []).some((e) => String(e.name || '').indexOf('Hello, E2E 中文 123') >= 0), lbl)

console.log('== checkbox toggle ==')
const cb = findEl(await run('rdp_uia', { action: 'find', text: 'Enable option' }), 'Enable option', 'CheckBox')
check('find: checkbox', !!cb, cb)
await run('rdp_uia', { action: 'click', i: cb.i })
const cbg = await run('rdp_uia', { action: 'get', i: cb.i })
check('toggle: checkbox on', cbg.toggle === 'on', cbg)

console.log('== combo selection ==')
const cmb = findEl(await run('rdp_uia', { action: 'find', text: 'Choice combo' }), 'Choice combo', 'ComboBox')
check('find: combo', !!cmb, cmb)
const exp = await run('rdp_uia', { action: 'click', i: cmb.i })
check('combo: expanded', exp.clicked === true && ['expand', 'mouse'].includes(exp.via), exp)
const item = findEl(await run('rdp_uia', { action: 'find', text: 'Gamma' }), 'Gamma', 'ListItem')
check('combo: item visible', !!item, item)
const sel = await run('rdp_uia', { action: 'click', i: item.i })
check('combo: item selected', sel.clicked === true, sel)
const cmb2 = await run('rdp_uia', { action: 'get', i: cmb.i })
check('combo: selection reported', cmb2.selection === 'Gamma', cmb2)

console.log('== dialog flow: click -> wait -> click -> wait gone ==')
const od = await run('rdp_uia', { action: 'find', text: 'Open Dialog' })
const odBtn = findEl(od, 'Open Dialog', 'Button')
check('find: open dialog', !!odBtn, od)
await run('rdp_uia', { action: 'click', i: odBtn.i })
const wd = await run('rdp_wait', { mode: 'element', text: 'Dialog OK', timeoutMs: 8000 })
check('wait: dialog appeared', wd.found === true && wd.i !== undefined, wd)
const dg = await run('rdp_uia', { action: 'get', i: wd.i })
check('dialog: ok button focused', dg.type === 'Button' && dg.name === 'Dialog OK', dg)
await run('rdp_uia', { action: 'click', i: wd.i })
const wg = await run('rdp_wait', { mode: 'element', text: 'Dialog OK', gone: true, timeoutMs: 6000 })
check('wait: dialog gone', wg.found === true && wg.gone === true, wg)

console.log('== wait modes ==')
const wi = await run('rdp_wait', { mode: 'idle', timeoutMs: 5000, stableMs: 500 })
check('wait: idle stable', wi.ok === true && wi.reason === 'stable', wi)
const ww = await run('rdp_wait', { mode: 'window', process: 'RdpTestUi', timeoutMs: 4000 })
check('wait: window found', ww.found === true && ww.window && ww.window.process === 'RdpTestUi', ww)
const wh = await run('rdp_wait', { mode: 'window', handle: hwnd, timeoutMs: 3000 })
check('wait: window by handle found', wh.found === true && wh.waitedMs < 1500, wh)
const wc = await run('rdp_wait', { mode: 'change', timeoutMs: 1200 })
check('wait: change times out when static', wc.ok === false && wc.reason === 'timeout', wc)

console.log('== mouse validation ==')
await expectError('mouse: drag without x2/y2 rejected', 'rdp_mouse', { action: 'drag', x: 10, y: 10 }, 'x2 and y2')
await expectError('mouse: click without x/y rejected', 'rdp_mouse', { action: 'click' }, 'requires x and y')

console.log('== screenshots ==')
const wrPre = await run('rdp_window', { action: 'rect', handle: hwnd })
const sf = await run('rdp_screenshot', { mode: 'full' })
check('screenshot: full file', sf.file && fs.existsSync(sf.file) && sf.width === 1920, { file: sf.file, width: sf.width })
const sw = await run('rdp_screenshot', { mode: 'window', process: 'RdpTestUi' })
check('screenshot: window mode', sw.file && fs.existsSync(sw.file), sw)
check('screenshot: window origin matches window rect', !!sw.origin && sw.origin.x === wrPre.x && sw.origin.y === wrPre.y, { origin: sw.origin, rect: wrPre })
const saveTarget = 'D:/workspace/dsh-rdp-wrapper/.tmp/e2e-save.png'
try { fs.unlinkSync(saveTarget) } catch { }
const ss = await run('rdp_screenshot', { mode: 'window', process: 'RdpTestUi', savePath: saveTarget })
check('screenshot: savePath honored', ss.savedTo === saveTarget && fs.existsSync(saveTarget) && fs.statSync(saveTarget).size > 1000, ss)

console.log('== clipboard round trip ==')
const cpayload = 'clip-' + stamp
await run('rdp_clipboard', { action: 'set', text: cpayload })
const cget = await run('rdp_clipboard', { action: 'get' })
check('clipboard: round trip', String(cget.text || '').indexOf(cpayload) >= 0, cget)

console.log('== key type after uia focus ==')
await run('rdp_uia', { action: 'focus', text: 'Name input' })
await run('rdp_key', { action: 'type', clear: true, text: 'typed via keys' })
const kg = await run('rdp_uia', { action: 'find', text: 'typed via keys' })
check('key: typed text visible', (kg.elements || []).some((e) => String(e.value || '').indexOf('typed via keys') >= 0), kg)
await run('rdp_uia', { action: 'setvalue', text: 'Name input', value: 'E2E 中文 123', clear: true })

console.log('== window tools ==')
const wl = await run('rdp_window', { action: 'list', process: 'RdpTestUi' })
const wins = Array.isArray(wl.windows) ? wl.windows : (Array.isArray(wl) ? wl : [])
check('window: list finds test app', wins.some((w) => (w.title || '').indexOf('RDP Test App') >= 0), wl)
check('window: process filter is strict', wins.length > 0 && wins.every((w) => w.process === 'RdpTestUi'), wins.map((w) => w.process))
const wf = await run('rdp_window', { action: 'focus', handle: hwnd })
check('window: focus by handle', wf.ok !== false, wf)
const wr = await run('rdp_window', { action: 'rect', handle: hwnd })
check('window: rect', !!wr && wr.w > 100 && wr.h > 100, wr)

console.log('== exec ==')
const ex = await run('rdp_exec', { command: 'echo ' + stamp })
check('exec: stdout', String(ex.stdout || '').indexOf(stamp) >= 0 && ex.exitcode === 0, ex)

console.log('== app alias launch (Paint) ==')
const paint = await run('rdp_app', { name: 'Paint', timeoutMs: 25000 })
check('app: alias Paint found', paint.found === true && (paint.window || {}).process === 'mspaint', paint)
if (paint.pid) {
  await run('rdp_exec', { command: 'taskkill /PID ' + paint.pid + ' /F' })
  await run('rdp_wait', { mode: 'window', process: 'mspaint', gone: true, timeoutMs: 8000 })
}

console.log('== batch rdp_do ==')
const batch = await run('rdp_do', {
  steps: [
    { tool: 'uia', args: { action: 'setvalue', text: 'Name input', value: 'Batch ' + stamp, clear: true } },
    { tool: 'uia', args: { action: 'click', text: 'Say Hello' } },
    { tool: 'wait', args: { mode: 'idle', timeoutMs: 4000, stableMs: 400 } },
  ],
})
check('do: all steps ok', batch.ok === true && batch.completed === 3 && batch.total === 3, batch)
const bl = await run('rdp_uia', { action: 'find', text: 'Hello, Batch ' + stamp })
check('do: batch effect visible', (bl.elements || []).some((e) => String(e.name || '').indexOf('Hello, Batch ' + stamp) >= 0), bl)

console.log('== stale index error ==')
await expectError('uia: stale index gives actionable error', 'rdp_uia', { action: 'get', i: 9999 }, 'rdp_state')

console.log('== cleanup ==')
const q = await run('rdp_uia', { action: 'find', text: 'Quit' })
const quit = findEl(q, 'Quit', 'Button')
if (quit) await run('rdp_uia', { action: 'click', i: quit.i })
const qg = await run('rdp_wait', { mode: 'window', process: 'RdpTestUi', gone: true, timeoutMs: 10000 })
check('cleanup: test app closed', qg.found === true && qg.gone === true, qg)
const whg = await run('rdp_wait', { mode: 'window', handle: hwnd, gone: true, timeoutMs: 3000 })
check('cleanup: stale handle reports gone fast', whg.found === true && whg.gone === true && whg.waitedMs < 1500, whg)

console.log('== app filter tolerance ==')
const appExe = await run('rdp_app', { path: uiExe, process: 'RdpTestUi.exe', timeoutMs: 20000 })
check('app: .exe process filter tolerated', appExe.found === true && (appExe.window || {}).process === 'RdpTestUi', appExe)
await run('rdp_exec', { command: 'taskkill /IM RdpTestUi.exe /F' })
await new Promise((r) => setTimeout(r, 800))
const appBad = await run('rdp_app', { path: uiExe, process: 'NoSuchApp.exe', timeoutMs: 20000 })
check('app: wrong filter falls back fast', appBad.found === true && appBad.filterIgnored === true && appBad.waitedMs < 8000, appBad)
await run('rdp_exec', { command: 'taskkill /IM RdpTestUi.exe /F' })

console.log('')
console.log('RESULT pass=' + pass + ' fail=' + fail)
if (fail) {
  console.log('FAILURES:')
  for (const f of failures) console.log(' - ' + f)
  process.exit(1)
}
