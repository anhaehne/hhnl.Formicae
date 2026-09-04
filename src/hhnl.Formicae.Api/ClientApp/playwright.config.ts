import { defineConfig, devices } from "@playwright/test";
import path from "node:path";

const apiUrl = "http://127.0.0.1:5000";
const uiUrl = "http://127.0.0.1:5173";

export default defineConfig({
  testDir: "./tests/e2e",
  outputDir: path.resolve(__dirname, "../../../test-results/playwright"),
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 1 : 0,
  reporter: "line",
  use: {
    baseURL: uiUrl,
    screenshot: "only-on-failure",
    trace: "retain-on-failure"
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] }
    }
  ],
  webServer: [
    {
      command: "dotnet run --project ../hhnl.Formicae.Api.csproj --no-launch-profile --urls http://127.0.0.1:5000",
      url: `${apiUrl}/healthz`,
      timeout: 120_000,
      reuseExistingServer: false,
      env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: "Development",
        UseFakeAdapters: "true",
        PersistenceMode: "InMemory",
        WorkflowDiscovery__Enabled: "false"
      }
    },
    {
      command: "npm run dev -- --host 127.0.0.1 --port 5173 --strictPort",
      url: uiUrl,
      timeout: 60_000,
      reuseExistingServer: false
    }
  ]
});
