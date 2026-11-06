const SOCKET_URL = "ws://127.0.0.1:17426/api/ws";
const RETRY_MIN_MS = 250, RETRY_MAX_MS = 5000;

export type StudioListener = { static(data: unknown): void; live(data: unknown): void; status(connected: boolean): void };

type Pending = { resolve(data: unknown): void; reject(error: Error): void };
type Message = { type: "static" | "live"; data: unknown } | { type: "reply"; id: number; status: number; data: { error?: string } | null };

let socket: WebSocket | null = null;
let open = false;
let reported: boolean | null = null;
let retryMs = RETRY_MIN_MS;
let retryTimer: number | null = null;
let nextId = 1;
const pending = new Map<number, Pending>();
const unsent: string[] = [];
const listeners = new Set<StudioListener>();
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

export function subscribe(listener: StudioListener): () => void {
  listeners.add(listener);
  connect();
  if (reported !== null) listener.status(reported);
  if (lastStatic !== null) listener.static(lastStatic);
  if (lastLive !== null) listener.live(lastLive);
  return () => { listeners.delete(listener); };
}
