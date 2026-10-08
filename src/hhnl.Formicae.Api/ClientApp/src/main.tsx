import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { createBrowserRouter, RouterProvider } from "react-router-dom";
import "@xyflow/react/dist/style.css";
import App from "./App";
import "./styles.css";
import { listWorkflowEventDefinitions } from "./api";
import { registerEventDefinitions } from "./workflowEvents";

const router = createBrowserRouter([{ path: "*", element: <App /> }]);

async function render() {
  try { registerEventDefinitions(await listWorkflowEventDefinitions()); } catch { /* Authentication may still be required; the editor retries loading metadata. */ }
  createRoot(document.getElementById("root")!).render(
    <StrictMode>
      <RouterProvider router={router} />
    </StrictMode>
  );
}
void render();
