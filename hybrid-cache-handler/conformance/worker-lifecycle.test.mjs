import { test } from 'node:test'
import assert from 'node:assert/strict'
import { spawn, spawnSync } from 'node:child_process'
import { createServer } from 'node:http'
import { createServer as createTcpServer } from 'node:net'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { setTimeout as delay } from 'node:timers/promises'

const root = dirname(fileURLToPath(import.meta.url))

async function poll (action) {
  const deadline = Date.now() + 15_000
  let error
  while (Date.now() < deadline) {
    try { return await action() } catch (ex) { error = ex; await delay(50) }
  }
  throw error
}

test('real Framework bridge preserves messages, cancels body reads, and fails health when its worker dies',
  { skip: process.platform !== 'win32', timeout: 120_000 }, async t => {
    for (const project of ['FrameworkWorker', 'ConformanceProxy']) {
      const build = spawnSync('dotnet', ['build', join(root, project, `${project}.csproj`), '-c', 'Release', '-v', 'q', '--nologo'], { stdio: 'inherit', windowsHide: true })
      assert.equal(build.status, 0, `${project} build failed: ${build.error ?? ''}`)
    }
    const contentRoot = await mkdtemp(join(root, '.content-store-lifecycle-'))
    let proxy
    let output = ''
    const body = Buffer.from([0, 1, 128, 255, 13, 10])
    let slowStarted
    const started = new Promise(resolve => { slowStarted = resolve })
    let slowClosed
    const closed = new Promise(resolve => { slowClosed = resolve })
    const origin = createServer(async (request, response) => {
      if (request.url.endsWith('/misstated-length')) {
        response.writeHead(200, { 'Content-Length': '1', 'Cache-Control': 'no-store' })
        response.end('body-longer-than-declared')
        return
      }
      if (request.url === '/close') { request.socket.destroy(); return }
      if (request.url === '/slow') {
        response.writeHead(200, { 'Content-Length': '1024', 'Cache-Control': 'no-store' })
        response.write('a')
        response.once('close', () => slowClosed())
        slowStarted()
        return
      }
      const chunks = []
      for await (const chunk of request) chunks.push(chunk)
      response.writeHead(203, 'Custom reason', {
        'Cache-Control': 'no-store',
        'Content-Type': 'application/octet-stream',
        'X-Received-Method': request.method,
        'Set-Cookie': ['a=1; Expires=Wed, 21 Oct 2037 07:28:00 GMT', 'b=2; Path=/']
      })
      response.end(Buffer.concat(chunks))
    })
    t.after(async () => {
      if (proxy && proxy.exitCode === null) {
        const kill = spawnSync('taskkill', ['/PID', String(proxy.pid), '/T', '/F'], { windowsHide: true })
        if (kill.status !== 0 && proxy.exitCode === null) t.diagnostic(`Cleanup taskkill: ${kill.stderr?.toString()}`)
        await proxy.done
      }
      origin.closeAllConnections()
      await new Promise(resolve => origin.close(resolve))
      await rm(contentRoot, { recursive: true, force: true })
      await writeFile(join(root, 'worker-lifecycle.log'), output)
      t.diagnostic('Worker output saved to conformance/worker-lifecycle.log')
    })
    await new Promise(resolve => origin.listen(0, '127.0.0.1', resolve))
    const reservation = createTcpServer()
    await new Promise(resolve => reservation.listen(0, '127.0.0.1', resolve))
    const port = reservation.address().port
    await new Promise(resolve => reservation.close(resolve))
    proxy = spawn('dotnet', [
      join(root, 'ConformanceProxy', 'bin', 'Release', 'net10.0', 'ConformanceProxy.dll'),
      '--framework', 'net472', '--port', String(port), '--origin', `http://127.0.0.1:${origin.address().port}`,
      '--file-system', 'false', '--content-root', contentRoot,
      '--worker', join(root, 'FrameworkWorker', 'bin', 'Release', 'net472', 'FrameworkWorker.exe')
    ], { stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true })
    proxy.done = new Promise((resolve, reject) => { proxy.once('error', reject); proxy.once('close', resolve) })
    proxy.stdout.on('data', chunk => { output += chunk })
    proxy.stderr.on('data', chunk => { output += chunk })
    const base = `http://127.0.0.1:${port}`
    const health = await poll(async () => {
      assert.equal(proxy.exitCode, null, output)
      const response = await fetch(`${base}/proxy-health`, { signal: AbortSignal.timeout(1000) })
      assert.equal(response.status, 200)
      return response.json()
    })
    assert.equal(health.framework, 'net472')
    assert.match(health.provenance, /\.NET Framework/)
    assert.equal((health.provenance.match(/\.NETFramework,Version=v4\.7\.2/g) ?? []).length, 3)
    assert.equal(health.processId, proxy.pid)
    assert.ok(Number.isInteger(health.workerProcessId))

    const echo = await fetch(`${base}/echo?a=1`, { method: 'PATCH', body })
    assert.equal(echo.status, 203)
    assert.equal(echo.headers.get('x-received-method'), 'PATCH')
    assert.deepEqual(echo.headers.getSetCookie(), ['a=1; Expires=Wed, 21 Oct 2037 07:28:00 GMT', 'b=2; Path=/'])
    assert.deepEqual(Buffer.from(await echo.arrayBuffer()), body)

    const malformed = await fetch(`${base}/test/11111111-1111-4111-8111-111111111111/misstated-length`)
    assert.equal(await malformed.text(), 'b')
    const afterMalformed = await fetch(`${base}/test/22222222-2222-4222-8222-222222222222/echo`, { method: 'PATCH', body })
    assert.equal(afterMalformed.status, 203, 'A malformed fixture must not poison another fixture connection pool.')
    assert.deepEqual(Buffer.from(await afterMalformed.arrayBuffer()), body)

    const disconnected = await fetch(`${base}/close`)
    assert.equal(disconnected.status, 502)
    assert.equal((await fetch(`${base}/proxy-health`)).status, 200, 'Origin transport failure must not poison worker health.')

    const cancellation = new AbortController()
    const slow = fetch(`${base}/slow`, { signal: cancellation.signal }).catch(error => error)
    await Promise.race([started, delay(10_000, null, { ref: false }).then(() => { throw new Error('Worker never reached slow origin.') })])
    cancellation.abort()
    assert.equal((await slow).name, 'AbortError')
    await Promise.race([closed, delay(10_000, null, { ref: false }).then(() => { throw new Error('Worker did not cancel the origin body read.') })])
    assert.equal((await fetch(`${base}/proxy-health`)).status, 200, 'Request cancellation must not poison worker health.')

    const kill = spawnSync('taskkill', ['/PID', String(health.workerProcessId), '/F'], { windowsHide: true })
    assert.equal(kill.status, 0, kill.stderr?.toString())
    await poll(async () => assert.equal((await fetch(`${base}/proxy-health`)).status, 503))
    assert.equal((await fetch(`${base}/echo`)).status, 502)
    assert.equal((await fetch(`${base}/proxy-health`)).status, 503, 'A failed worker must never produce a healthy full-suite gate.')
  })
