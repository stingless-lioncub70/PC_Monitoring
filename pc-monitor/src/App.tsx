import { Dashboard } from "./components/Dashboard";
import { Overlay } from "./components/Overlay";

export default function App() {
  const view = new URLSearchParams(window.location.search).get("view");
  return view === "overlay" ? <Overlay /> : <Dashboard />;
}
