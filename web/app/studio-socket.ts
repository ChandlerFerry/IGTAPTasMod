/**
 * The studio's one connection to the game plugin: a WebSocket on /api/ws (WebStudioServer.cs). The plugin pushes the
 * static state (`{type: "static"}`) when it changes and the live state (`{type: "live"}`) whenever it differs; commands go
 * out as `{id, method, path, body}` and resolve with their `{type: "reply", id}`. A dropped socket (the game closed or
 * restarted) connects again, waiting longer after each failure.
 */
const SOCKET_URL = "ws://127.0.0.1:17426/api/ws";
const RETRY_MIN_MS = 250, RETRY_MAX_MS = 5000;

export type StudioListener = { static(data: unknown): void; live(data: unknown): void; status(connected: boolean): void };

type Pending = { resolve(data: unknown): void; reject(error: Error): void };
type Message = { type: "static" | "live"; data: unknown } | { type: "reply"; id: number; status: number; data: { error?: string } | null };

let socket: WebSocket | null = null;
let open = false;
// The status last given to the listeners: a failed reconnect doesn't repeat "offline".
let reported: boolean | null = null;
let retryMs = RETRY_MIN_MS;
let retryTimer: number | null = null;
let nextId = 1;
const pending = new Map<number, Pending>();
// Requests made while the socket connects, sent once it opens.
const unsent: string[] = [];
const listeners = new Set<StudioListener>();
// This connection's last pushed parts, for a listener that subscribes after they came.
let lastStatic: unknown = null, lastLive: unknown = null;

function connect() {
  if (socket || retryTimer !== null) return;
  const ws = socket = new WebSocket(SOCKET_URL);
  ws.onopen = () => {
    open = true;
    retryMs = RETRY_MIN_MS;
    for (const message of unsent.splice(0)) ws.send(message);
    reported = true;
    listeners.forEach(l => l.status(true));
  };
  ws.onmessage = event => {
    const message: Message = JSON.parse(event.data);
    if (message.type === "reply") {
      const request = pending.get(message.id);
      if (!request) return;
      pending.delete(message.id);
      const data = message.data;
      if (message.status < 200 || message.status >= 300 || data?.error) request.reject(new Error(data?.error || `Request failed (${message.status})`));
      else request.resolve(data);
    }
    else if (message.type === "static") { lastStatic = message.data; listeners.forEach(l => l.static(message.data)); }
    else if (message.type === "live") { lastLive = message.data; listeners.forEach(l => l.live(message.data)); }
  };
  ws.onclose = () => {
    socket = null;
    open = false;
    lastStatic = lastLive = null;
    unsent.length = 0;
    const offline = new Error("Game plugin offline");
    pending.forEach(request => request.reject(offline));
    pending.clear();
    if (reported !== false) { reported = false; listeners.forEach(l => l.status(false)); }
    retryTimer = window.setTimeout(() => { retryTimer = null; connect(); }, retryMs);
    retryMs = Math.min(retryMs * 2, RETRY_MAX_MS);
  };
}

/**
 * Sends a command (`path` below /api, e.g. `/tas/advance`): a GET without a body, a POST with one. Resolves with the
 * handler's result (typed by each caller, as fetch's json() was); rejects with its error, or when the game is offline.
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function request(path: string, body?: unknown): Promise<any> {
  connect();
  const ws = socket;
  if (!ws) return Promise.reject(new Error("Game plugin offline"));
  const id = nextId++;
  const message = JSON.stringify(body === undefined ? { id, method: "GET", path: `/api${path}` } : { id, method: "POST", path: `/api${path}`, body });
  const { promise, resolve, reject } = Promise.withResolvers<unknown>();
  pending.set(id, { resolve, reject });
  if (open) ws.send(message); else unsent.push(message);
  return promise;
}

/** Listens for the pushed state and the connection's status; the state already pushed comes at once. Returns the unsubscribe. */
export function subscribe(listener: StudioListener): () => void {
  listeners.add(listener);
  connect();
  if (reported !== null) listener.status(reported);
  if (lastStatic !== null) listener.static(lastStatic);
  if (lastLive !== null) listener.live(lastLive);
  return () => { listeners.delete(listener); };
}
