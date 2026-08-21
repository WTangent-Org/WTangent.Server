<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch, nextTick } from 'vue'

interface Msg {
  role: 'user' | 'assistant' | 'thinking' | 'tool'
  text: string
  name?: string
  collapsed?: boolean
  time?: string
  elapsed?: string
  streaming?: boolean
  startTime?: number
}

interface Session {
  id: string
  title: string
  count: number
  inputTokens: number
  outputTokens: number
  cacheHitTokens: number
}

const server = ref('')
const serverOptions = ref<string[]>(['http://127.0.0.1:8890（回环）'])
const user = ref('')
const projects = ref<string[]>([])
const sessions = ref<Session[]>([])
const sessionId = ref('')
const autoOptimize = ref(false)
const messages = ref<Msg[]>([])
const input = ref('')
const busy = ref(false)
const status = ref('')
const connected = ref(false)
const sidebarTab = ref<'chat' | 'project' | 'settings'>('chat')
const msgsEl = ref<HTMLElement | null>(null)
let ws: WebSocket | null = null
let followBottom = true

const base = () => server.value || location.origin
const wsBase = () => base().replace(/^http/, 'ws')

const now = () => new Date().toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })

async function api(path: string, init?: RequestInit): Promise<Response> {
  const r = await fetch(base() + path, { headers: { 'Content-Type': 'application/json' }, ...init })
  if (!r.ok) throw new Error((await r.text()).slice(0, 300))
  return r
}

async function connect() {
  status.value = '连接中…'
  try {
    const r = await (await api('/projects')).json()
    projects.value = r.projects ?? []
    try {
      const rr = await (await api('/remotes')).json()
      serverOptions.value = ['http://127.0.0.1:8890（回环）', ...(rr.remotes ?? []).map((x: any) => `${x.name}: ${x.url}`)]
    } catch { /* 旧 serve 无 /remotes，忽略 */ }
    try {
      const rc = await (await api('/config')).json()
      autoOptimize.value = rc.auto_optimize ?? false
    } catch { /* 旧 serve 无 /config，忽略 */ }
    await refreshSessions()
    connected.value = true
    status.value = `已连接 ${base()}`
  } catch (e: any) {
    connected.value = false
    status.value = '连接失败: ' + e.message
  }
}

async function refreshSessions() {
  try {
    const r = await (await api('/sessions')).json()
    sessions.value = (r.sessions ?? []).map((s: any) => ({
      id: s.id,
      title: s.title ?? '',
      count: s.count ?? 0,
      inputTokens: s.input_tokens ?? 0,
      outputTokens: s.output_tokens ?? 0,
      cacheHitTokens: s.cache_hit_tokens ?? 0,
    }))
  } catch { /* 旧 serve 无 /sessions */ }
}

async function newSession() {
  try {
    const r = await (await api('/session', { method: 'POST' })).json()
    sessionId.value = r.session_id
    messages.value = []
    await refreshSessions()
    openWs()
    status.value = `新会话 ${sessionId.value.slice(0, 8)}`
  } catch (e: any) { status.value = '创建失败: ' + e.message }
}

async function switchSession(id: string) {
  if (id === sessionId.value) return
  ws?.close()
  ws = null
  sessionId.value = id
  messages.value = []
  // 加载历史消息（SQLite 持久化，续聊）
  try {
    const r = await (await api(`/session/${id}/messages`)).json()
    for (const m of (r.messages ?? [])) {
      if (m.role === 'user') messages.value.push({ role: 'user', text: m.content, time: now() })
      else if (m.role === 'assistant') messages.value.push({ role: 'assistant', text: m.content, time: now() })
    }
  } catch { /* 旧 serve 无 messages 端点 */ }
  openWs()
  status.value = `会话 ${id.slice(0, 8)}`
}

function last(): Msg | undefined { return messages.value[messages.value.length - 1] }

function onWsEvent(d: any) {
  switch (d.type) {
    case 'message_delta': {
      if (last()?.role !== 'assistant') messages.value.push({ role: 'assistant', text: '', time: now(), streaming: true })
      const m = last()!
      m.text += d.text ?? ''
      m.streaming = true
      break
    }
    case 'reasoning_delta': {
      if (last()?.role !== 'thinking') messages.value.push({ role: 'thinking', text: '', collapsed: true, time: now() })
      last()!.text += d.text ?? ''
      break
    }
    case 'tool_start': {
      messages.value.push({ role: 'tool', name: `${d.name} ${d.arguments ?? ''}`.trim(), text: '', collapsed: true, time: now(), streaming: true, startTime: Date.now() })
      break
    }
    case 'tool_end': {
      const m = last()
      if (m?.role === 'tool') {
        m.text = (d.result ?? '').slice(0, 3000)
        m.streaming = false
        if (m.startTime) m.elapsed = ((Date.now() - m.startTime) / 1000).toFixed(1) + 's'
      }
      break
    }
    case 'confirm_req': {
      const allow = window.confirm(d.prompt ?? '危险命令，允许执行？')
      ws?.send(JSON.stringify({ type: 'confirm', id: d.id, allow }))
      break
    }
    case 'turn_end': {
      const m = last()
      if (m?.role === 'assistant') m.streaming = false
      busy.value = false
      break
    }
    case 'done':
      busy.value = false
      break
    case 'error':
      messages.value.push({ role: 'assistant', text: '[error] ' + (d.text ?? '') })
      busy.value = false
      break
  }
}

async function copyText(text: string) {
  try {
    await navigator.clipboard.writeText(text)
    status.value = '已复制'
  } catch {
    // 非 https/localhost 环境 clipboard 不可用：textarea 兜底
    const ta = document.createElement('textarea')
    ta.value = text
    document.body.appendChild(ta)
    ta.select()
    document.execCommand('copy')
    document.body.removeChild(ta)
    status.value = '已复制'
  }
}

function openWs() {
  if (!sessionId.value) return
  ws = new WebSocket(wsBase() + '/ws/' + sessionId.value)
  ws.onmessage = (ev) => { try { onWsEvent(JSON.parse(ev.data)) } catch { /* 忽略坏帧 */ } }
  ws.onclose = () => { if (busy.value) { busy.value = false; messages.value.push({ role: 'assistant', text: '[连接断开]' }) } }
  ws.onerror = () => ws?.close()
}

async function send() {
  const text = input.value.trim()
  if (!text || busy.value) return
  input.value = ''
  messages.value.push({ role: 'user', text, time: now() })
  followBottom = true
  scrollToBottom()
  if (!sessionId.value) {
    const r = await (await api('/session', { method: 'POST' })).json()
    sessionId.value = r.session_id
    await refreshSessions()
    openWs()
  }
  busy.value = true
  ws?.send(JSON.stringify({ type: 'ask', text }))
}

function stop() {
  ws?.send(JSON.stringify({ type: 'cancel' }))
}

/// 底部跟随（dsh FOLLOW_THRESHOLD=24px）：接近底部自动跟随，用户上滚阅读时暂停
function onScroll() {
  const el = msgsEl.value
  if (!el) return
  followBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 24
}

function scrollToBottom() {
  const el = msgsEl.value
  if (el) el.scrollTop = el.scrollHeight
}

async function setBranch(p: string) {
  if (!user.value) return alert('先填用户名（作为分支名）')
  status.value = `切换分支 ${user.value}…`
  try {
    const r = await (await api(`/projects/${p}/branch?name=${encodeURIComponent(user.value)}`, { method: 'POST' })).json()
    status.value = r.ok ? `项目 ${p} → 分支 ${r.branch} ✓` : '失败: ' + (r.error ?? JSON.stringify(r))
  } catch (e: any) { status.value = '失败: ' + e.message }
}

async function doPush(p: string) {
  if (!user.value) return alert('先填用户名')
  status.value = `push ${p}…`
  try {
    const r = await (await api(`/projects/${p}/push?name=${encodeURIComponent(user.value)}`, { method: 'POST' })).json()
    status.value = r.ok ? `push ${p} ✓` : 'push 失败: ' + (r.error ?? (r.problems ?? []).join('；'))
  } catch (e: any) { status.value = '失败: ' + e.message }
}

async function saveAutoOptimize() {
  try {
    const r = await (await api('/config', { method: 'POST', body: JSON.stringify({ auto_optimize: autoOptimize.value }) })).json()
    status.value = r.ok ? `自动优化：${r.auto_optimize ? '开' : '关'} ✓` : '保存失败: ' + JSON.stringify(r)
  } catch (e: any) { status.value = '保存失败: ' + e.message }
}

/// 极简 markdown：转义 → 代码块 → 表格 → 引用 → 列表 → 加粗/行内代码 → 换行
function md(t: string): string {
  const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
  // 代码块 ```lang ... ```
  const parts = t.split(/```(\w*)\n?([\s\S]*?)```/)
  let html = ''
  for (let i = 0; i < parts.length; i++) {
    if (i % 3 === 0) html += esc(parts[i])
    else if (i % 3 === 1) html += ''
    else html += `<pre class="code"><code>${esc(parts[i]).trim()}</code></pre>`
  }
  // 表格：| a | b | + 分隔行 → <table>
  html = html.replace(/((?:\|.*\|(?:\n|$))+)/g, (block: string) => {
    const rows = block.trim().split('\n').filter(r => r.trim().startsWith('|'))
    if (rows.length < 2) return block
    const cells = (r: string) => r.trim().replace(/^\||\|$/g, '').split('|').map(c => c.trim())
    const header = cells(rows[0])
    const body = rows.slice(1).filter(r => !/^\|[\s:|-]+\|$/.test(r)).map(cells)
    if (body.length === 0) return block
    const tr = (cs: string[]) => `<tr>${cs.map(c => `<td>${c}</td>`).join('')}</tr>`
    return `<table><thead>${tr(header)}</thead><tbody>${body.map(tr).join('')}</tbody></table>`
  })
  return html
    .replace(/^&gt; (.+)$/gm, '<blockquote>$1</blockquote>')
    .replace(/^(\d+)\. (.+)$/gm, '<li class="ol">$2</li>')
    .replace(/\*\*([^*]+)\*\*/g, '<b>$1</b>')
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/^- (.+)$/gm, '<li>$1</li>')
    .replace(/\n/g, '<br>')
}

/// dsh 风格 Think 折叠摘要：流式中显示最新行，否则显示首行
function thinkSummary(m: Msg): string {
  const text = m.text.trimEnd()
  if (text.length === 0) return ''
  if (m.streaming) {
    const nl = text.lastIndexOf('\n')
    return nl === -1 ? text : text.slice(nl + 1)
  }
  const nl = text.indexOf('\n')
  return nl === -1 ? text : text.slice(0, nl)
}

const sessionLabel = (s: Session) => (s.title || s.id.slice(0, 8)) + (s.count > 0 ? ` · ${s.count}` : '')

// 消息变化时自动跟随底部（用户上滚阅读时暂停）
watch(messages, async () => {
  if (!followBottom) return
  await nextTick()
  scrollToBottom()
}, { deep: true })

onMounted(connect)
onUnmounted(() => ws?.close())
</script>

<template>
  <div class="app">
    <!-- 顶部栏：服务器 + 连接状态 + 用户 -->
    <header>
      <div class="brand">agent</div>
      <input v-model="server" list="server-list" placeholder="服务器地址（空 = 同源）" />
      <datalist id="server-list">
        <option v-for="o in serverOptions" :key="o" :value="o" />
      </datalist>
      <input v-model="user" placeholder="用户名（即分支名）" />
      <button @click="connect">连接</button>
      <span class="dot" :class="connected ? 'ok' : 'bad'" :title="status"></span>
      <span class="status">{{ status }}</span>
    </header>

    <div class="body">
      <!-- 左侧边栏：聊天 / 项目 / 设置 三个 tab -->
      <aside>
        <nav class="tabs">
          <button :class="{ active: sidebarTab === 'chat' }" @click="sidebarTab = 'chat'">会话</button>
          <button :class="{ active: sidebarTab === 'project' }" @click="sidebarTab = 'project'">项目</button>
          <button :class="{ active: sidebarTab === 'settings' }" @click="sidebarTab = 'settings'">设置</button>
        </nav>

        <template v-if="sidebarTab === 'chat'">
          <div class="side-head">
            <span>会话（{{ sessions.length }}）</span>
            <button class="mini" @click="newSession" title="新建会话">＋</button>
          </div>
          <ul class="side-list">
            <li v-for="s in sessions" :key="s.id" :class="{ cur: s.id === sessionId }" @click="switchSession(s.id)">
              <span class="sess">{{ sessionLabel(s) }}</span>
              <span v-if="s.id === sessionId" class="cur-mark">●</span>
            </li>
          </ul>
        </template>

        <template v-else-if="sidebarTab === 'project'">
          <div class="side-head"><span>项目（{{ projects.length }}）</span></div>
          <ul class="side-list">
            <li v-for="p in projects" :key="p">
              <span class="proj">{{ p }}</span>
              <button class="mini" @click="setBranch(p)" title="切换到我的开发分支">分支</button>
              <button class="mini" @click="doPush(p)" title="主动 push 我的分支">push</button>
            </li>
          </ul>
          <p class="hint">LLM 由 serve 调用<br />多用户：每人一个开发分支</p>
        </template>

        <template v-else>
          <div class="side-head"><span>设置</span></div>
          <label class="opt" title="收到 git push 后由 agent 自动审查并做简单优化（消耗 token）">
            <input type="checkbox" v-model="autoOptimize" @change="saveAutoOptimize" /> 自动优化（push 后）
          </label>
          <p class="hint">开启后每次 push 都会触发 agent 审查优化；默认关省 token</p>
        </template>
      </aside>

      <!-- 消息区 -->
      <main>
        <div class="msgs" ref="msgsEl" @scroll="onScroll">
          <div v-for="(m, i) in messages" :key="i" :class="'msg ' + m.role">
            <div v-if="m.role === 'tool'" class="tool-card" :class="{ open: !m.collapsed }">
              <div class="tool-head" @click="m.collapsed = !m.collapsed">
                <span class="tool-icon">⚙</span>
                <span class="tool-name">{{ m.name }}</span>
                <span v-if="m.elapsed" class="tool-elapsed">{{ m.elapsed }}</span>
                <span class="tool-time">{{ m.time }}</span>
                <span class="chev">{{ m.collapsed ? '▸' : '▾' }}</span>
              </div>
              <div v-if="!m.collapsed" class="tool-body"><pre>{{ m.text }}</pre></div>
            </div>
            <div v-else-if="m.role === 'thinking'" class="thinking-card">
              <div class="think-row" @click="m.collapsed = !m.collapsed">
                <span class="tool-icon">🧠</span>
                <span class="tool-name">Think</span>
                <span v-if="!m.collapsed" class="think-summary">{{ thinkSummary(m) }}</span>
                <span class="chev">{{ m.collapsed ? '▸' : '▾' }}</span>
              </div>
              <div v-if="!m.collapsed" class="tool-body thinking">{{ m.text }}</div>
            </div>
            <div v-else-if="m.role === 'user'" class="bubble user">
              <div class="bubble-head">你 <span class="t">{{ m.time }}</span>
                <button class="icon-btn" title="复制" @click="copyText(m.text)">⧉</button>
              </div>
              <div class="bubble-body">{{ m.text }}</div>
            </div>
            <div v-else class="bubble assistant">
              <div class="bubble-head">agent <span class="t">{{ m.time }}</span>
                <button class="icon-btn" title="复制" @click="copyText(m.text)">⧉</button>
              </div>
              <div class="bubble-body" v-html="md(m.text)"></div>
              <span v-if="m.streaming" class="cursor">▊</span>
            </div>
          </div>
          <div v-if="messages.length === 0" class="empty">
            <div class="empty-logo">agent</div>
            <p>聊点什么吧——LLM 由 serve 调用</p>
          </div>
        </div>
        <footer>
          <textarea v-model="input" rows="3" placeholder="输入消息…（Enter 发送，Shift+Enter 换行）" @keydown.enter.exact.prevent="send"></textarea>
          <div class="footer-btns">
            <button v-if="messages.length" class="ghost" @click="messages = []" title="清空消息区">清空</button>
            <button v-if="busy" @click="stop" class="danger">停止</button>
            <button @click="send" :disabled="busy">{{ busy ? '思考中…' : '发送' }}</button>
          </div>
        </footer>
      </main>
    </div>
  </div>
</template>

<style>
* { box-sizing: border-box; }
body { margin: 0; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', 'PingFang SC', sans-serif; background: #14141a; color: #d7dae0; }
.app { display: flex; flex-direction: column; height: 100vh; }

/* 顶部栏 */
header { display: flex; gap: 8px; padding: 8px 14px; background: #1b1b22; align-items: center; border-bottom: 1px solid #2a2a35; }
header .brand { font-weight: 700; color: #7aa2f7; font-size: 15px; margin-right: 8px; }
header input { flex: 1; max-width: 280px; padding: 5px 10px; background: #23232d; color: inherit; border: 1px solid #32323f; border-radius: 6px; }
header input:focus { outline: none; border-color: #7aa2f7; }
button { padding: 5px 12px; background: #2e3a55; color: #dbe6ff; border: 1px solid #3d4c70; border-radius: 6px; cursor: pointer; font-size: 13px; }
button:hover { background: #3a4a6e; }
button:disabled { opacity: .5; cursor: default; }
button.mini { padding: 2px 8px; font-size: 12px; }
button.danger { background: #552e2e; border-color: #703d3d; }
.dot { width: 8px; height: 8px; border-radius: 50%; display: inline-block; }
.dot.ok { background: #9ece6a; }
.dot.bad { background: #f7768e; }
.status { margin-left: auto; font-size: 12px; color: #8b91a0; max-width: 40%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

/* 主体两栏 */
.body { display: flex; flex: 1; min-height: 0; }

/* 左侧边栏 */
aside { width: 230px; border-right: 1px solid #2a2a35; display: flex; flex-direction: column; background: #181820; }
aside .tabs { display: flex; padding: 8px; gap: 4px; border-bottom: 1px solid #2a2a35; }
aside .tabs button { flex: 1; background: transparent; border: none; color: #8b91a0; font-size: 13px; padding: 5px 0; border-radius: 6px; }
aside .tabs button:hover { background: #23232d; }
aside .tabs button.active { background: #2e3a55; color: #dbe6ff; }
aside .side-head { display: flex; justify-content: space-between; align-items: center; padding: 10px 12px 6px; font-size: 12px; color: #8b91a0; }
aside ul { list-style: none; margin: 0; padding: 0 8px; overflow-y: auto; flex: 1; }
aside li { display: flex; gap: 6px; align-items: center; padding: 6px 8px; margin-bottom: 2px; border-radius: 6px; font-size: 13px; cursor: pointer; }
aside li:hover { background: #23232d; }
aside li.cur { background: #2e3a55; color: #dbe6ff; }
aside .proj, aside .sess { flex: 1; font-family: monospace; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
aside .cur-mark { color: #7aa2f7; font-size: 10px; }
aside .hint { font-size: 11px; color: #5d6370; margin: 12px; line-height: 1.6; }
aside .opt { display: flex; gap: 8px; align-items: center; font-size: 13px; cursor: pointer; padding: 0 12px; }

/* 消息区 */
main { flex: 1; display: flex; flex-direction: column; min-width: 0; }
.msgs { flex: 1; overflow-y: auto; padding: 16px 20px; display: flex; flex-direction: column; gap: 12px; }
.empty { margin: auto; text-align: center; color: #5d6370; }
.empty-logo { font-size: 28px; font-weight: 700; color: #2e3a55; margin-bottom: 8px; }

/* 气泡 */
.bubble { max-width: 780px; }
.bubble-head { font-size: 11px; color: #8b91a0; margin-bottom: 4px; }
.bubble-head .t { margin-left: 6px; color: #5d6370; }
.bubble-body { font-size: 14px; line-height: 1.7; white-space: pre-wrap; }
.bubble.user .bubble-body { color: #fff; }
.bubble.assistant .bubble-body :deep(b) { color: #fff; }
.bubble-body :deep(code) { background: #23232d; padding: 1px 5px; border-radius: 4px; font-size: 12px; }
.bubble-body :deep(pre.code) { background: #101015; color: #a8b3c9; padding: 10px 14px; border-radius: 8px; overflow-x: auto; font-size: 12px; border: 1px solid #2a2a35; }
.bubble-body :deep(pre.code code) { background: transparent; padding: 0; }
.bubble-body :deep(li) { display: block; margin-left: 16px; }

/* 工具/思考卡片 */
.tool-card, .thinking-card { max-width: 780px; background: #1e1e28; border: 1px solid #2f2f3d; border-radius: 8px; overflow: hidden; }
.tool-head { display: flex; gap: 8px; align-items: center; padding: 7px 12px; cursor: pointer; font-size: 12px; color: #9fb4e8; }
.tool-head:hover { background: #23232f; }
.tool-icon { opacity: .8; }
.tool-name { font-family: monospace; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.tool-time { color: #5d6370; font-size: 11px; }
.tool-elapsed { color: #7aa2f7; font-size: 11px; }
.chev { color: #5d6370; }
.tool-body { border-top: 1px solid #2f2f3d; }
.tool-body pre { margin: 0; padding: 10px 12px; background: #101015; color: #a8b3c9; overflow-x: auto; font-size: 12px; white-space: pre-wrap; }
.tool-body.thinking { padding: 10px 12px; color: #8b91a0; font-size: 13px; white-space: pre-wrap; line-height: 1.6; }

/* dsh 风格 Think 折叠行：标题 + 首行摘要（流式中最新行） */
.thinking-card { border-radius: 8px; }
.think-row { display: flex; gap: 8px; align-items: center; padding: 7px 12px; cursor: pointer; font-size: 12px; color: #9fb4e8; }
.think-row:hover { background: #23232f; }
.think-summary { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: #6d7484; font-size: 12px; }

/* 复制按钮 + 流式光标 */
.icon-btn { background: transparent; border: none; color: #5d6370; cursor: pointer; font-size: 12px; padding: 0 4px; margin-left: 6px; }
.icon-btn:hover { color: #9fb4e8; }
.cursor { color: #7aa2f7; animation: blink 1s step-end infinite; font-size: 13px; }
@keyframes blink { 50% { opacity: 0; } }

/* markdown 表格/引用 */
.bubble-body :deep(table) { border-collapse: collapse; margin: 8px 0; font-size: 13px; }
.bubble-body :deep(td) { border: 1px solid #2f2f3d; padding: 4px 10px; }
.bubble-body :deep(thead td) { background: #23232d; color: #9fb4e8; font-weight: 600; }
.bubble-body :deep(blockquote) { border-left: 3px solid #3d4c70; margin: 8px 0; padding: 2px 12px; color: #8b91a0; }
.bubble-body :deep(li.ol) { display: block; margin-left: 16px; list-style: decimal; }

/* 输入区 */
footer { display: flex; gap: 8px; padding: 10px 14px; border-top: 1px solid #2a2a35; background: #1b1b22; }
footer textarea { flex: 1; resize: none; padding: 8px 10px; background: #23232d; color: inherit; border: 1px solid #32323f; border-radius: 8px; font-family: inherit; font-size: 14px; }
.footer-btns { display: flex; gap: 8px; align-items: flex-end; }
button.ghost { background: transparent; border-color: #32323f; color: #8b91a0; }
button.ghost:hover { background: #23232d; }
footer textarea:focus { outline: none; border-color: #7aa2f7; }
</style>
