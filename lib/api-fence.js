/**
 * Trust fence for the pet's HTTP API.
 *
 * `webServer.register` is a route carrier: it hands a request to the handler
 * without authenticating the source, so a route mounted on it has to decide for
 * itself whether the request may be served. The loopback check inside the
 * handler only proves the connection came from this machine, which DNS
 * rebinding defeats — an attacker hostname that re-resolves to 127.0.0.1 gives
 * a loopback `remoteAddress` while the browser still treats the call as
 * same-origin, so no CORS rule applies either.
 *
 * `ctx.connection.requestRejection` is the composition's own answer to that:
 * the Host header rules out rebinding, the Origin rules out cross-site calls,
 * and the browser login cookie requires a real session. It returns the status
 * to answer with (401/403), or undefined when the route may serve the request.
 *
 * Read through `ctx.get` rather than declaring a dependency: a headless
 * composition ships no `connection` service, and the plugin still has work to
 * do there (the pet process is not web-only). With no service the fence cannot
 * judge anything, and the loopback check in the handler stays the boundary.
 *
 * @param {object} ctx plugin context
 * @param {{ headers: object }} req incoming request
 * @returns {401 | 403 | undefined} rejection status, or undefined when admitted
 */
export function rejectionOf(ctx, req) {
  let connection
  try {
    connection = typeof ctx?.get === 'function' ? ctx.get('connection') : undefined
  } catch {
    connection = undefined
  }
  if (typeof connection?.requestRejection !== 'function') return undefined
  try {
    const rejection = connection.requestRejection(req)
    return rejection === 401 || rejection === 403 ? rejection : undefined
  } catch {
    // A fence that cannot judge a request must not open the route.
    return 403
  }
}
