export class RouteError extends Error {
  constructor(code, message, status = 422, details = undefined) {
    super(message)
    this.name = 'RouteError'
    this.code = code
    this.status = status
    this.details = details
  }
}

export function invariantRoute(condition, code, message, status = 422, details = undefined) {
  if (!condition) throw new RouteError(code, message, status, details)
}
