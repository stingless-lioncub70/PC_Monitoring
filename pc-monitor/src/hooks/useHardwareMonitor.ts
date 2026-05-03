import { useEffect, useRef, useState } from "react";
import type { ConnectionStatus, Telemetry } from "../types/telemetry";

interface UseHardwareMonitor {
  data: Telemetry | null;
  status: ConnectionStatus;
  error: string | null;
}

const RECONNECT_BASE_MS = 1000;
const RECONNECT_MAX_MS = 10_000;

export function useHardwareMonitor(url: string): UseHardwareMonitor {
  const [data, setData] = useState<Telemetry | null>(null);
  const [status, setStatus] = useState<ConnectionStatus>("connecting");
  const [error, setError] = useState<string | null>(null);

  const wsRef = useRef<WebSocket | null>(null);
  const reconnectAttempt = useRef(0);
  const reconnectTimer = useRef<number | null>(null);
  const closedByUnmount = useRef(false);

  useEffect(() => {
    closedByUnmount.current = false;

    const connect = () => {
      setStatus("connecting");
      let socket: WebSocket;
      try {
        socket = new WebSocket(url);
      } catch (e) {
        setStatus("error");
        setError(e instanceof Error ? e.message : "Failed to construct WebSocket");
        return;
      }
      wsRef.current = socket;

      socket.onopen = () => {
        reconnectAttempt.current = 0;
        setStatus("open");
        setError(null);
      };

      socket.onmessage = (ev) => {
        try {
          const parsed = JSON.parse(ev.data) as Telemetry;
          setData(parsed);
        } catch {
          // ignore malformed frame
        }
      };

      socket.onerror = () => {
        setStatus("error");
        setError(
          "WebSocket error — is backend/monitor.py running on ws://localhost:8765?",
        );
      };

      socket.onclose = () => {
        if (closedByUnmount.current) return;
        setStatus("closed");
        const delay = Math.min(
          RECONNECT_BASE_MS * 2 ** reconnectAttempt.current,
          RECONNECT_MAX_MS,
        );
        reconnectAttempt.current += 1;
        reconnectTimer.current = window.setTimeout(connect, delay);
      };
    };

    connect();

    return () => {
      closedByUnmount.current = true;
      if (reconnectTimer.current != null) window.clearTimeout(reconnectTimer.current);
      wsRef.current?.close();
    };
  }, [url]);

  return { data, status, error };
}
