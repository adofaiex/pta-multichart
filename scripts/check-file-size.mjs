#!/usr/bin/env node
// 文件大小钩子：单文件行数超阈值就报警 / 拦住。
// 用法：
//   node scripts/check-file-size.mjs                  报告（永远退出 0）
//   node scripts/check-file-size.mjs --fail            超失败阈值退出 1
//   node scripts/check-file-size.mjs --fail=600 --warn=300 --top=20
//   node scripts/check-file-size.mjs --dir=src
// 退出码：0 通过 / 1 有文件超失败阈值 / 2 环境问题（不是 git 仓库、git 不可用）
import { execFileSync } from 'node:child_process'
import { readFileSync, statSync } from 'node:fs'
import { extname, resolve } from 'node:path'

const raw = Object.fromEntries(
  process.argv.slice(2).map((a) => {
    const [k, v] = a.replace(/^--/, '').split('=')
    return [k, v ?? true]
  }),
)

// 裸 --fail 表示"开启闸门、用默认阈值"，不是阈值 1
function num(name, fallback) {
  const v = raw[name]
  if (v === undefined || v === true) return fallback
  const n = Number(v)
  return Number.isFinite(n) && n > 0 ? n : fallback
}

const WARN = num('warn', 400)
const FAIL = num('fail', 800)
const TOP = num('top', 15)
const GATE = raw.fail !== undefined // 出现 --fail 就开闸
const ROOT = typeof raw.dir === 'string' ? resolve(raw.dir) : process.cwd()

const CODE = new Set([
  '.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs', '.vue', '.svelte',
  '.py', '.go', '.rs', '.java', '.kt', '.cs', '.rb', '.php', '.swift',
  '.c', '.h', '.cc', '.cpp', '.hpp', '.m', '.mm', '.lua', '.dart', '.sh',
])

const SKIP = [
  /(^|\/)node_modules\//, /(^|\/)vendor\//, /(^|\/)dist\//, /(^|\/)build\//,
  /(^|\/)out\//, /(^|\/)coverage\//, /(^|\/)\.git\//, /(^|\/)\.dsh\//,
  /\.min\.(js|css)$/, /\.bundle\.(js|css)$/, /\.generated\.[^/]+$/,
  /_pb2\.py$/, /\.d\.ts$/, /(^|\/)pnpm-lock\.yaml$/, /(^|\/)package-lock\.json$/,
  /(^|\/)yarn\.lock$/, /(^|\/)Cargo\.lock$/,
]

function gitTrackedFiles() {
  try {
    // stdio 显式指定：否则 git 自己那行 "fatal: not a git repository" 会漏到用户终端
    const out = execFileSync('git', ['ls-files', '-z'], {
      cwd: ROOT,
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe'],
    })
    return out.split('\0').filter(Boolean).map((f) => resolve(ROOT, f))
  } catch (err) {
    const msg = String(err?.stderr ?? err?.message ?? err)
    if (/not a git repository/i.test(msg)) {
      console.error(`不在 git 仓库里（${ROOT}）。先 git init，或者用 --dir= 指到仓库根目录。`)
    } else if (err?.code === 'ENOENT') {
      console.error('找不到 git 命令，先确认 git 在 PATH 里（git --version 能跑通）。')
    } else {
      console.error('git ls-files 失败：' + msg.split('\n')[0])
    }
    process.exit(2)
  }
}

function lineCount(file) {
  let buf
  try {
    buf = readFileSync(file)
  } catch {
    return null
  }
  if (buf.includes(0)) return null // 二进制
  let n = 0
  for (let i = 0; i < buf.length; i++) if (buf[i] === 10) n++
  return buf.length && buf[buf.length - 1] !== 10 ? n + 1 : n
}

const rows = []
for (const file of gitTrackedFiles()) {
  const rel = file.slice(ROOT.length).replace(/^[\\/]/, '').replaceAll('\\', '/')
  if (!CODE.has(extname(file).toLowerCase())) continue
  if (SKIP.some((re) => re.test(rel))) continue
  let size = 0
  try {
    size = statSync(file).size
  } catch {
    continue
  }
  if (size > 2 * 1024 * 1024) continue
  const lines = lineCount(file)
  if (lines === null) continue
  rows.push({ rel, lines })
}

rows.sort((a, b) => b.lines - a.lines)
const over = rows.filter((r) => r.lines > WARN)
const hard = rows.filter((r) => r.lines > FAIL)

console.log(`检查范围: ${ROOT}`)
console.log(`阈值: 警告 > ${WARN} 行, 失败 > ${FAIL} 行${GATE ? '（开闸）' : '（只报告）'}`)
console.log(`源码文件: ${rows.length} 个, 总行数: ${rows.reduce((s, r) => s + r.lines, 0)}`)
console.log('')
if (rows.length === 0) {
  console.log('没找到可统计的源码文件（换 --dir= 试试）。')
} else if (over.length === 0) {
  console.log(`最大的 ${Math.min(TOP, rows.length)} 个文件，全部在 ${WARN} 行以内。`)
  for (const r of rows.slice(0, TOP)) console.log(`          ${String(r.lines).padStart(6)}  ${r.rel}`)
} else {
  console.log(`最大的 ${Math.min(TOP, rows.length)} 个文件:`)
  for (const r of rows.slice(0, TOP)) {
    const mark = r.lines > FAIL ? 'FAIL' : r.lines > WARN ? 'WARN' : '    '
    console.log(`  ${mark}  ${String(r.lines).padStart(6)}  ${r.rel}`)
  }
  console.log('')
  if (hard.length) {
    console.log(`超过失败阈值（> ${FAIL}）：${hard.length} 个`)
    for (const r of hard.slice(0, 10)) console.log(`  - ${r.rel} (${r.lines} 行)`)
    console.log('下一步：把这个文件按职责拆开，拆完再提交。')
  }
  if (over.length > hard.length) {
    console.log(`超过警告阈值（> ${WARN}）：${over.length - hard.length} 个（不阻塞）`)
  }
}

if (GATE && hard.length) process.exitCode = 1
