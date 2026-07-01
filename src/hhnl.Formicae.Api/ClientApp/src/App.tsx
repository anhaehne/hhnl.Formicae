import { FormEvent, useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { Dispatch, ReactNode, SetStateAction } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import {
  addConnectedRepository,
  AiSettings,
  CodexAuthSetupStatus,
  ConnectedRepository,
  createInvite,
  createGitHubIntegration,
  CurrentUser,
  deleteConnectedRepository,
  deleteIntegration,
  getAiSettings,
  getAppVersion,
  getCodexAuthConnectionStatus,
  getCurrentUser,
  getIntegration,
  getWorkflow,
  GitHubUserRepository,
  IntegrationDetail,
  IntegrationSummary,
  listManagementRoles,
  listManagementUsers,
  listChatMessages,
  listEvents,
  listGitHubUserRepositories,
  listIntegrations,
  listLogs,
  listRuns,
  listSignals,
  listWorkflows,
  listWorkflowDefinitions,
  logout,
  ManagementRole,
  ManagementUser,
  redeemInvite,
  restartIdentityProvider,
  retryTaskRun,
  retryWorkflow,
  rotateWebhookSecret,
  setIdentityProviderEnabled,
  startCodexAuthConnection,
  startWorkflow,
  TaskRun,
  updateAiSettings,
  updateManagementUserRoles,

  WorkflowChatMessage,
  WorkflowDefinitionResponse,
  WorkflowEvent,
  WorkflowLog,
  WorkflowSignal,
  WorkflowSummary
} from "./api";
import WorkflowDefinitionsPage from "./WorkflowDefinitionsPage";
import { getEnabledDefinitionVersions } from "./workflowGraph";

const workflowStatuses = ["Queued", "Planning", "Implementing", "CreatingPullRequest", "Reviewing", "Completed", "Failed", "Canceled"];
const workflowSteps = ["None", "Plan", "Implement", "CreatePullRequest", "AddressComments", "Done"];
const taskRunKinds = ["Plan", "Implement", "CreatePullRequest", "AddressComments"];
const taskRunStatuses = ["Queued", "Running", "Succeeded", "Failed"];

type FormState = {
  issueUrl: string;
  repositoryUrl: string;
  baseBranch: string;
  model: string;
  workflowDefinitionVersionId: string;
};

type AiSettingsFormState = {
  name: string;
  provider: string;
  model: string;
  endpointUrl: string;
  agentKind: string;
  acpProvider: string;
  acpCommand: string;
  authMethod: string;
  llmApiKeySecretName: string;
  llmApiKey: string;
  apiKeyEnvironmentVariable: string;
  subscriptionCredentialJson: string;
  subscriptionCredentialFileName: string;
  subscriptionCredentialMountPath: string;
};

type Page = "workflows" | "workflow-definitions" | "integrations" | "repositories" | "users" | "settings";

const pages: Page[] = ["workflows", "workflow-definitions", "integrations", "repositories", "users", "settings"];

const pagePaths: Record<Page, string> = {
  "workflows": "/workflows",
  "workflow-definitions": "/workflow-definitions",
  "integrations": "/integrations",
  "repositories": "/repositories",
  "users": "/users",
  "settings": "/settings"
};

type GitHubIntegrationFormState = {
  displayName: string;
  clientId: string;
  clientSecretReference: string;
  privateKey: string;
};

type DetailState = {
  workflow?: WorkflowSummary;
  runs: TaskRun[];
  logs: WorkflowLog[];
  events: WorkflowEvent[];
  signals: WorkflowSignal[];
  chatMessages: WorkflowChatMessage[];
  loading: boolean;
  error?: string;
};

const initialForm: FormState = {
  issueUrl: "",
  repositoryUrl: "",
  baseBranch: "main",
  model: "",
  workflowDefinitionVersionId: ""
};

const initialAiSettingsForm: AiSettingsFormState = {
  name: "New AI",
  provider: "",
  model: "",
  endpointUrl: "",
  agentKind: "OpenHands",
  acpProvider: "ClaudeCode",
  acpCommand: "",
  authMethod: "ApiKey",
  llmApiKeySecretName: "",
  llmApiKey: "",
  apiKeyEnvironmentVariable: "",
  subscriptionCredentialJson: "",
  subscriptionCredentialFileName: "",
  subscriptionCredentialMountPath: ""
};

const initialGitHubIntegrationForm: GitHubIntegrationFormState = {
  displayName: "GitHub",
  clientId: "",
  clientSecretReference: "",
  privateKey: ""
};

export default function App() {
  const location = useLocation();
  const navigate = useNavigate();
  const [activePage, setActivePage] = useState<Page>("workflows");
  const [menuOpen, setMenuOpen] = useState(false);
  const [form, setForm] = useState<FormState>(initialForm);
  const modelTouched = useRef(false);
  const [aiSettingsList, setAiSettingsList] = useState<AiSettings[]>([]);
  const [selectedAiSettingsId, setSelectedAiSettingsId] = useState<string>();
  const [aiSettingsForm, setAiSettingsForm] = useState<AiSettingsFormState>(initialAiSettingsForm);
  const [loadingAiSettings, setLoadingAiSettings] = useState(false);
  const [savingAiSettings, setSavingAiSettings] = useState(false);
  const [aiSettingsError, setAiSettingsError] = useState<string>();
  const [aiSettingsSaved, setAiSettingsSaved] = useState<string>();
  const [codexAuthConnection, setCodexAuthConnection] = useState<CodexAuthSetupStatus>();
  const [startingCodexAuth, setStartingCodexAuth] = useState(false);
  const [workflows, setWorkflows] = useState<WorkflowSummary[]>([]);
  const [workflowDefinitions, setWorkflowDefinitions] = useState<WorkflowDefinitionResponse[]>([]);
  const [selectedWorkflowId, setSelectedWorkflowId] = useState<string>();
  const [detail, setDetail] = useState<DetailState>({ runs: [], logs: [], events: [], signals: [], chatMessages: [], loading: false });
  const [loadingWorkflows, setLoadingWorkflows] = useState(false);
  const [loadingWorkflowDefinitions, setLoadingWorkflowDefinitions] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [retryingRunId, setRetryingRunId] = useState<string>();
  const [retryingWorkflowId, setRetryingWorkflowId] = useState<string>();
  const [formError, setFormError] = useState<string>();
  const [listError, setListError] = useState<string>();
  const [workflowDefinitionError, setWorkflowDefinitionError] = useState<string>();
  const [workflowDefinitionSaved, setWorkflowDefinitionSaved] = useState<string>();
  const [integrations, setIntegrations] = useState<IntegrationSummary[]>([]);
  const [selectedIntegrationId, setSelectedIntegrationId] = useState<string>();
  const [integrationDetail, setIntegrationDetail] = useState<IntegrationDetail>();
  const [loadingIntegrations, setLoadingIntegrations] = useState(false);
  const [integrationError, setIntegrationError] = useState<string>();
  const [integrationSaved, setIntegrationSaved] = useState<string>();
  const [creatingIntegration, setCreatingIntegration] = useState(false);
  const [deletingIntegration, setDeletingIntegration] = useState(false);
  const [restartingIdentityProvider, setRestartingIdentityProvider] = useState(false);
  const [githubIntegrationForm, setGitHubIntegrationForm] = useState<GitHubIntegrationFormState>(initialGitHubIntegrationForm);
  const [availableRepositories, setAvailableRepositories] = useState<GitHubUserRepository[]>([]);
  const [repositorySearch, setRepositorySearch] = useState("");
  const [connectedRepositorySearch, setConnectedRepositorySearch] = useState("");
  const [repositoryError, setRepositoryError] = useState<string>();
  const [repositorySaved, setRepositorySaved] = useState<string>();
  const [loadingAvailableRepositories, setLoadingAvailableRepositories] = useState(false);
  const [addingRepositoryUrl, setAddingRepositoryUrl] = useState<string>();
  const [removingRepositoryId, setRemovingRepositoryId] = useState<string>();
  const [currentUser, setCurrentUser] = useState<CurrentUser>();
  const [authError, setAuthError] = useState<string>();
  const [inviteCode, setInviteCode] = useState<string>();
  const [inviteLink, setInviteLink] = useState<string>();
  const [redeemCode, setRedeemCode] = useState("");
  const [authBusy, setAuthBusy] = useState(false);
  const [appVersion, setAppVersion] = useState<string>();
  const [managementRoles, setManagementRoles] = useState<ManagementRole[]>([]);
  const [managementUsers, setManagementUsers] = useState<ManagementUser[]>([]);
  const [loadingManagementUsers, setLoadingManagementUsers] = useState(false);
  const [updatingManagementUserId, setUpdatingManagementUserId] = useState<string>();
  const [userManagementError, setUserManagementError] = useState<string>();
  const [userManagementSaved, setUserManagementSaved] = useState<string>();

  const processedInvite = useRef<string | undefined>(undefined);
  const processedIdentityActivation = useRef<string | undefined>(undefined);
  const loginRedirectStarted = useRef(false);

  const canViewWorkflows = currentUser?.canViewWorkflows === true;
  const canTriggerWorkflows = currentUser?.canTriggerWorkflows === true;
  const canAdminister = currentUser?.canAdminister === true;
  const replaceUrlParams = useCallback((values: Record<string, string | undefined>) => {
    navigate(buildReturnUrl(values), { replace: true });
  }, [navigate]);
  const navigationItems = [
    { page: "workflows", label: "Workflows", disabled: false },
    { page: "workflow-definitions", label: "Definitions", disabled: !canViewWorkflows },
    { page: "integrations", label: "Integrations", disabled: !canAdminister },
    { page: "repositories", label: "Repositories", disabled: !canAdminister },
    { page: "users", label: "Users", disabled: false },
    { page: "settings", label: "Settings", disabled: !canAdminister }
  ] satisfies Array<{ page: Page; label: string; disabled: boolean }>;

  const selectedWorkflow = useMemo(
    () => detail.workflow ?? workflows.find(workflow => workflow.workflowId === selectedWorkflowId),
    [detail.workflow, selectedWorkflowId, workflows]
  );
  const selectedAiSettings = useMemo(
    () => aiSettingsList.find(settings => settings.id === selectedAiSettingsId) ?? aiSettingsList[0],
    [aiSettingsList, selectedAiSettingsId]
  );
  const failureEvents = useMemo(
    () => detail.events.filter(event => (event.type === "WorkflowFailed" || event.level === "Error") && event.detailsJson),
    [detail.events]
  );
  const enabledDefinitionVersions = useMemo(
    () => getEnabledDefinitionVersions(workflowDefinitions),
    [workflowDefinitions]
  );
  const refreshCurrentUser = useCallback(async () => {
    try {
      setCurrentUser(await getCurrentUser());
      setAuthError(undefined);
    } catch (error) {
      setAuthError(error instanceof Error ? error.message : "Could not load current user.");
    }
  }, []);

  useEffect(() => {
    void refreshCurrentUser();
  }, [refreshCurrentUser]);

  useEffect(() => {
    let ignore = false;
    getAppVersion()
      .then(result => {
        if (!ignore) {
          setAppVersion(result.version);
        }
      })
      .catch(() => {
        if (!ignore) {
          setAppVersion("unknown");
        }
      });

    return () => {
      ignore = true;
    };
  }, []);
  useEffect(() => {
    if (!currentUser || currentUser.authenticated || !currentUser.authRequired || loginRedirectStarted.current) {
      return;
    }

    loginRedirectStarted.current = true;
    handleLogin(window.location.pathname + window.location.search);
  }, [currentUser]);

  useEffect(() => {
    const query = window.matchMedia("(min-width: 961px)");
    const closeMenu = () => {
      if (query.matches) {
        setMenuOpen(false);
      }
    };

    closeMenu();
    query.addEventListener("change", closeMenu);
    return () => query.removeEventListener("change", closeMenu);
  }, []);

  const refreshWorkflows = useCallback(async () => {
    setLoadingWorkflows(true);
    setListError(undefined);
    try {
      const recent = await listWorkflows(25);
      setWorkflows(recent);
      if (!selectedWorkflowId && recent.length > 0) {
        setSelectedWorkflowId(recent[0].workflowId);
      }
    } catch (error) {
      setListError(error instanceof Error ? error.message : "Could not load workflows.");
    } finally {
      setLoadingWorkflows(false);
    }
  }, [selectedWorkflowId]);

  const refreshWorkflowDefinitions = useCallback(async (definitionId?: string, versionId?: string) => {
    setLoadingWorkflowDefinitions(true);
    setWorkflowDefinitionError(undefined);
    try {
      const definitions = await listWorkflowDefinitions();
      setWorkflowDefinitions(definitions);
      setForm(current => {
        if (current.workflowDefinitionVersionId || definitions.length === 0) {
          return current;
        }

        const defaultVersion = getEnabledDefinitionVersions(definitions)[0];
        return {
          ...current,
          workflowDefinitionVersionId: defaultVersion?.version.id ?? ""
        };
      });
    } catch (error) {
      setWorkflowDefinitionError(error instanceof Error ? error.message : "Could not load workflow definitions.");
    } finally {
      setLoadingWorkflowDefinitions(false);
    }
  }, []);

  useEffect(() => {
    if (!currentUser || !canViewWorkflows) {
      return;
    }

    void refreshWorkflows();
    void refreshWorkflowDefinitions();
  }, [canViewWorkflows, currentUser, refreshWorkflowDefinitions, refreshWorkflows]);

  useEffect(() => {
    const params = new URLSearchParams(location.search);
    const routePage = parsePagePath(location.pathname);
    const targetPage =
      params.get("installationId") ? "repositories" :
      params.get("inviteRedeemed") === "true" || params.get("inviteError") ? "users" :
      routePage ?? "workflows";
    params.delete("page");
    const query = params.toString();
    const targetSearch = query ? `?${query}` : "";

    if (location.pathname !== pagePaths[targetPage] || location.search !== targetSearch) {
      navigate(`${pagePaths[targetPage]}${targetSearch}`, { replace: true });
      return;
    }

    setActivePage(targetPage);
    setMenuOpen(false);
  }, [location.pathname, location.search, navigate]);

  useEffect(() => {
    const params = new URLSearchParams(location.search);
    const integrationId = params.get("integrationId");
    const installationId = params.get("installationId");
    const setupAction = params.get("setupAction");
    const invite = params.get("invite");
    const inviteRedeemed = params.get("inviteRedeemed");
    const inviteError = params.get("inviteError");

    if (integrationId) {
      setSelectedIntegrationId(integrationId);
    }

    if (installationId) {
      setRepositorySaved(`GitHub App ${setupAction ?? "installation"} completed for installation ${installationId}.`);
    }

    if (invite) {
      setRedeemCode(invite);
    }

    if (inviteRedeemed === "true") {
      setAuthError(undefined);
    }

    if (inviteError) {
      setAuthError(inviteError);
    }
  }, [location.search]);
  useEffect(() => {
    if (!codexAuthConnection || codexAuthConnection.status !== "Running") {
      return;
    }

    let ignore = false;
    const interval = window.setInterval(() => {
      getCodexAuthConnectionStatus(codexAuthConnection.aiSettingsId, codexAuthConnection.jobName)
        .then(async status => {
          if (ignore) {
            return;
          }

          setCodexAuthConnection(status);
          if (status.status === "Succeeded") {
            const settings = await getAiSettings();
            if (!ignore) {
              setAiSettingsList(settings);
              const selected = settings.find(item => item.id === status.aiSettingsId);
              if (selected) {
                setAiSettingsForm(toAiSettingsForm(selected));
              }

              setAiSettingsSaved("Codex subscription connected.");
            }
          }
        })
        .catch(error => {
          if (!ignore) {
            setAiSettingsError(error instanceof Error ? error.message : "Could not refresh Codex login status.");
          }
        });
    }, 3000);

    return () => {
      ignore = true;
      window.clearInterval(interval);
    };
  }, [codexAuthConnection]);

  useEffect(() => {
    let ignore = false;
    async function loadAiSettings() {
      setLoadingAiSettings(true);
      setAiSettingsError(undefined);
      try {
        const settings = await getAiSettings();
        if (ignore) {
          return;
        }

        setAiSettingsList(settings);
        const firstSettings = settings[0];
        setSelectedAiSettingsId(firstSettings?.id);
        setAiSettingsForm(firstSettings ? toAiSettingsForm(firstSettings) : initialAiSettingsForm);
        setForm(current => {
          if (!firstSettings || modelTouched.current || current.model.trim()) {
            return current;
          }

          return { ...current, model: firstSettings.model ?? "" };
        });
      } catch (error) {
        if (!ignore) {
          setAiSettingsError(error instanceof Error ? error.message : "Could not load AI settings.");
        }
      } finally {
        if (!ignore) {
          setLoadingAiSettings(false);
        }
      }
    }

    void loadAiSettings();
    return () => {
      ignore = true;
    };
  }, []);

  const refreshIntegrations = useCallback(async () => {
    setLoadingIntegrations(true);
    setIntegrationError(undefined);
    try {
      const items = await listIntegrations();
      setIntegrations(items);
      const nextSelectedId = selectedIntegrationId ?? items[0]?.id;
      setSelectedIntegrationId(nextSelectedId);
      if (nextSelectedId) {
        const detail = await getIntegration(nextSelectedId);
        setIntegrationDetail(detail);
      } else {
        setIntegrationDetail(undefined);
      }
    } catch (error) {
      setIntegrationError(error instanceof Error ? error.message : "Could not load integrations.");
    } finally {
      setLoadingIntegrations(false);
    }
  }, [selectedIntegrationId]);

  const refreshUserManagement = useCallback(async () => {
    if (!canAdminister) {
      setManagementRoles([]);
      setManagementUsers([]);
      return;
    }

    setLoadingManagementUsers(true);
    setUserManagementError(undefined);
    try {
      const [roles, users] = await Promise.all([listManagementRoles(), listManagementUsers()]);
      setManagementRoles(roles);
      setManagementUsers(users);
    } catch (error) {
      setUserManagementError(error instanceof Error ? error.message : "Could not load users and roles.");
    } finally {
      setLoadingManagementUsers(false);
    }
  }, [canAdminister]);

  const refreshAvailableRepositories = useCallback(async () => {
    if (!selectedIntegrationId) {
      setAvailableRepositories([]);
      return;
    }

    setLoadingAvailableRepositories(true);
    setRepositoryError(undefined);
    try {
      setAvailableRepositories(await listGitHubUserRepositories(selectedIntegrationId));
    } catch (error) {
      setRepositoryError(error instanceof Error ? error.message : "Could not load GitHub repositories.");
    } finally {
      setLoadingAvailableRepositories(false);
    }
  }, [selectedIntegrationId]);

  useEffect(() => {
    if (!canAdminister) {
      return;
    }

    if (activePage === "integrations" || activePage === "repositories") {
      void refreshIntegrations();
    }
    if (activePage === "repositories") {
      void refreshAvailableRepositories();
    }
    if (activePage === "users") {
      void refreshUserManagement();
    }
  }, [activePage, canAdminister, refreshAvailableRepositories, refreshIntegrations, refreshUserManagement]);

  useEffect(() => {
    if (!currentUser) {
      return;
    }

    const params = new URLSearchParams(location.search);
    const invite = params.get("invite");
    if (!invite || processedInvite.current === invite || !currentUser.authenticated || currentUser.authorized) {
      return;
    }

    processedInvite.current = invite;
    setAuthBusy(true);
    setAuthError(undefined);
    redeemInvite(invite)
      .then(async () => {
        setRedeemCode("");
        await refreshCurrentUser();
        replaceUrlParams({ page: "users", inviteRedeemed: "true" });
      })
      .catch(error => {
        setAuthError(error instanceof Error ? error.message : "Could not redeem invite.");
        replaceUrlParams({ page: "users", inviteError: error instanceof Error ? error.message : "Could not redeem invite." });
      })
      .finally(() => setAuthBusy(false));
  }, [currentUser, location.search, refreshCurrentUser, replaceUrlParams]);

  useEffect(() => {
    if (!currentUser?.authenticated) {
      return;
    }

    const params = new URLSearchParams(location.search);
    const integrationId = params.get("enableIdentityProviderId");
    if (!integrationId || processedIdentityActivation.current === integrationId) {
      return;
    }

    processedIdentityActivation.current = integrationId;
    navigate(pagePaths.integrations, { replace: true });
    setSelectedIntegrationId(integrationId);
    setIntegrationError(undefined);
    setIntegrationSaved(undefined);
    setIdentityProviderEnabled(integrationId, true)
      .then(async integration => {
        setIntegrationDetail(integration);
        setIntegrationSaved("Identity provider enabled and current user authorized.");
        await refreshCurrentUser();
        await refreshIntegrations();
        replaceUrlParams({ page: "integrations", integrationId });
      })
      .catch(error => {
        setIntegrationError(error instanceof Error ? error.message : "Could not enable identity provider.");
        replaceUrlParams({ page: "integrations", integrationId });
      });
  }, [currentUser?.authenticated, location.search, navigate, refreshCurrentUser, refreshIntegrations, replaceUrlParams]);

  useEffect(() => {
    if (!selectedWorkflowId) {
      setDetail({ runs: [], logs: [], events: [], signals: [], chatMessages: [], loading: false });
      return;
    }

    const workflowId = selectedWorkflowId;
    let ignore = false;
    async function loadDetail(showLoading = true) {
      if (showLoading) {
        setDetail(current => ({ ...current, loading: true, error: undefined }));
      }
      try {
        const [workflow, runs, logs, events, signals, chatMessages] = await Promise.all([
          getWorkflow(workflowId),
          listRuns(workflowId),
          listLogs(workflowId),
          listEvents(workflowId),
          listSignals(workflowId),
          listChatMessages(workflowId)
        ]);
        if (!ignore) {
          setDetail({ workflow, runs, logs, events, signals, chatMessages, loading: false });
        }
      } catch (error) {
        if (!ignore) {
          setDetail({
            workflow: workflows.find(workflow => workflow.workflowId === workflowId),
            runs: [],
            logs: [],
            events: [],
            signals: [],
            chatMessages: [],
            loading: false,
            error: error instanceof Error ? error.message : "Could not load workflow details."
          });
        }
      }
    }

    void loadDetail();
    const refreshInterval = window.setInterval(() => {
      void loadDetail(false);
    }, 3000);
    return () => {
      ignore = true;
      window.clearInterval(refreshInterval);
    };
  }, [selectedWorkflowId, workflows]);

  async function refreshWorkflowDetail(workflowId: string) {
    const [workflow, runs, logs, events, signals, chatMessages] = await Promise.all([
      getWorkflow(workflowId),
      listRuns(workflowId),
      listLogs(workflowId),
      listEvents(workflowId),
      listSignals(workflowId),
      listChatMessages(workflowId)
    ]);
    setDetail({ workflow, runs, logs, events, signals, chatMessages, loading: false });
  }

  async function handleRetryRun(run: TaskRun) {
    const workflowId = detail.workflow?.workflowId ?? selectedWorkflowId;
    if (!workflowId) {
      return;
    }

    setRetryingRunId(run.id);
    setDetail(current => ({ ...current, error: undefined }));
    try {
      const workflow = await retryTaskRun(workflowId, run.id);
      setDetail(current => ({ ...current, workflow, loading: false }));
      await refreshWorkflowDetail(workflowId);
      await refreshWorkflows();
    } catch (error) {
      setDetail(current => ({
        ...current,
        loading: false,
        error: error instanceof Error ? error.message : "Could not retry task run."
      }));
    } finally {
      setRetryingRunId(undefined);
    }
  }

  async function handleRetryWorkflow(workflow: WorkflowSummary) {
    setSelectedWorkflowId(workflow.workflowId);
    setRetryingWorkflowId(workflow.workflowId);
    setListError(undefined);
    setDetail(current => ({ ...current, error: undefined }));
    try {
      const retriedWorkflow = await retryWorkflow(workflow.workflowId);
      setDetail(current => ({ ...current, workflow: retriedWorkflow, loading: false }));
      await refreshWorkflowDetail(workflow.workflowId);
      await refreshWorkflows();
    } catch (error) {
      setListError(error instanceof Error ? error.message : "Could not retry workflow.");
    } finally {
      setRetryingWorkflowId(undefined);
    }
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setFormError(undefined);

    if (!form.issueUrl.trim() || !form.repositoryUrl.trim()) {
      setFormError("Issue URL and repository URL are required.");
      return;
    }

    const selectedDefinitionVersion = enabledDefinitionVersions.find(item => item.version.id === form.workflowDefinitionVersionId);
    if (enabledDefinitionVersions.length > 0 && !selectedDefinitionVersion) {
      setFormError("Select an enabled workflow definition before starting.");
      return;
    }
    if (enabledDefinitionVersions.length === 0) {
      setFormError("No enabled workflow definition versions are available.");
      return;
    }

    setSubmitting(true);
    try {
      const workflowDefinition = selectedDefinitionVersion;
      if (!workflowDefinition) {
        throw new Error("Select an enabled workflow definition before starting.");
      }

      const workflow = await startWorkflow({
        issueUrl: form.issueUrl.trim(),
        repositoryUrl: form.repositoryUrl.trim(),
        baseBranch: form.baseBranch.trim() || "main",
        model: form.model.trim() || null,
        workflowDefinitionId: workflowDefinition.definition.id,
        workflowDefinitionVersionId: workflowDefinition.version.id
      });
      setSelectedWorkflowId(workflow.workflowId);
      setForm(current => ({ ...current, issueUrl: "", model: "" }));
      await refreshWorkflows();
    } catch (error) {
      setFormError(error instanceof Error ? error.message : "Could not start workflow.");
    } finally {
      setSubmitting(false);
    }
  }

  function handleSelectAiSettings(settingsId: string) {
    const settings = aiSettingsList.find(item => item.id === settingsId);
    if (!settings) {
      return;
    }

    setSelectedAiSettingsId(settings.id);
    setAiSettingsForm(toAiSettingsForm(settings));
    setAiSettingsError(undefined);
    setAiSettingsSaved(undefined);
  }

  function handleNewAiSettings() {
    const nextId = createAiSettingsId();
    setSelectedAiSettingsId(nextId);
    setAiSettingsForm({ ...initialAiSettingsForm, name: `AI ${aiSettingsList.length + 1}` });
    setAiSettingsError(undefined);
    setAiSettingsSaved(undefined);
  }

  async function handleStartCodexAuthConnection() {
    if (!selectedAiSettings?.id) {
      setAiSettingsError("Save the AI profile before starting Codex login.");
      return;
    }

    setStartingCodexAuth(true);
    setAiSettingsError(undefined);
    setAiSettingsSaved(undefined);
    try {
      const status = await startCodexAuthConnection(selectedAiSettings.id);
      setCodexAuthConnection(status);
      setAiSettingsSaved("Codex login started. Complete the browser login shown in the output.");
    } catch (error) {
      setAiSettingsError(error instanceof Error ? error.message : "Could not start Codex login.");
    } finally {
      setStartingCodexAuth(false);
    }
  }
  async function handleAiSettingsSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setAiSettingsError(undefined);
    setAiSettingsSaved(undefined);
    setSavingAiSettings(true);

    const settingsId = selectedAiSettingsId ?? createAiSettingsId();
    const settingsName = aiSettingsForm.name.trim() || "New AI";

    try {
      const settings = await updateAiSettings({
        id: settingsId,
        name: settingsName,
        provider: aiSettingsForm.provider.trim() || null,
        model: aiSettingsForm.model.trim() || null,
        endpointUrl: aiSettingsForm.endpointUrl.trim() || null,
        agentKind: aiSettingsForm.agentKind,
        acpProvider: aiSettingsForm.agentKind === "Acp" ? aiSettingsForm.acpProvider : null,
        acpCommand: aiSettingsForm.acpCommand.trim() || null,
        authMethod: aiSettingsForm.authMethod,
        llmApiKeySecretName: aiSettingsForm.llmApiKeySecretName.trim() || null,
        llmApiKey: aiSettingsForm.llmApiKey || null,
        apiKeyEnvironmentVariable: aiSettingsForm.apiKeyEnvironmentVariable.trim() || null,
        subscriptionCredentialJson: aiSettingsForm.subscriptionCredentialJson || null,
        subscriptionCredentialFileName: aiSettingsForm.subscriptionCredentialFileName.trim() || null,
        subscriptionCredentialMountPath: aiSettingsForm.subscriptionCredentialMountPath.trim() || null,
        codexAuthJson: aiSettingsForm.authMethod === "CodexSubscription" ? aiSettingsForm.subscriptionCredentialJson || null : null
      });
      setSelectedAiSettingsId(settings.id);
      setAiSettingsList(current => {
        const existingIndex = current.findIndex(item => item.id === settings.id);
        if (existingIndex < 0) {
          return [...current, settings];
        }

        return current.map(item => item.id === settings.id ? settings : item);
      });
      setAiSettingsForm(toAiSettingsForm(settings));
      setForm(current => {
        if (modelTouched.current || current.model.trim()) {
          return current;
        }

        return { ...current, model: settings.model ?? "" };
      });
      setAiSettingsSaved("Saved. New workflow executions use the first configured AI.");
    } catch (error) {
      setAiSettingsError(error instanceof Error ? error.message : "Could not save AI settings.");
    } finally {
      setSavingAiSettings(false);
    }
  }

  async function handleCreateIntegration(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setIntegrationError(undefined);
    setIntegrationSaved(undefined);
    setCreatingIntegration(true);
    try {
      const integration = await createGitHubIntegration({
        displayName: githubIntegrationForm.displayName.trim() || "GitHub",
        clientId: githubIntegrationForm.clientId.trim(),
        clientSecretReference: githubIntegrationForm.clientSecretReference.trim() || null,
        privateKey: githubIntegrationForm.privateKey.trim(),
        webhookSecret: null
      });
      setIntegrationDetail(integration);
      setSelectedIntegrationId(integration.id);
      setGitHubIntegrationForm(initialGitHubIntegrationForm);
      setIntegrationSaved("GitHub integration created.");
      await refreshIntegrations();
    } catch (error) {
      setIntegrationError(error instanceof Error ? error.message : "Could not create integration.");
    } finally {
      setCreatingIntegration(false);
    }
  }

  async function handleSelectIntegration(integrationId: string) {
    setSelectedIntegrationId(integrationId);
    setIntegrationError(undefined);
    setIntegrationSaved(undefined);
    try {
      const detail = await getIntegration(integrationId);
      setIntegrationDetail(detail);
    } catch (error) {
      setIntegrationError(error instanceof Error ? error.message : "Could not load integration.");
    }
  }

  async function handleRotateWebhookSecret() {
    if (!selectedIntegrationId) {
      return;
    }

    setIntegrationError(undefined);
    setIntegrationSaved(undefined);
    try {
      setIntegrationDetail(await rotateWebhookSecret(selectedIntegrationId));
      setIntegrationSaved("Webhook secret rotated.");
    } catch (error) {
      setIntegrationError(error instanceof Error ? error.message : "Could not rotate webhook secret.");
    }
  }

  async function handleDeleteIntegration() {
    if (!selectedIntegrationId || !integrationDetail) {
      return;
    }

    const confirmed = window.confirm(`Remove ${integrationDetail.displayName}? Connected repositories for this integration will also be removed.`);
    if (!confirmed) {
      return;
    }

    setIntegrationError(undefined);
    setIntegrationSaved(undefined);
    setDeletingIntegration(true);
    try {
      await deleteIntegration(selectedIntegrationId);
      setSelectedIntegrationId(undefined);
      setIntegrationDetail(undefined);
      setRepositorySaved(undefined);
      setIntegrationSaved("Integration removed.");
      await refreshIntegrations();
    } catch (error) {
      setIntegrationError(error instanceof Error ? error.message : "Could not remove integration.");
    } finally {
      setDeletingIntegration(false);
    }
  }

  async function handleAddRepository(repository: GitHubUserRepository) {
    if (!selectedIntegrationId) {
      setRepositoryError("Select a GitHub integration before adding repositories.");
      return;
    }

    setRepositoryError(undefined);
    setRepositorySaved(undefined);
    setAddingRepositoryUrl(repository.repositoryUrl);
    try {
      await addConnectedRepository(selectedIntegrationId, {
        repositoryUrl: repository.repositoryUrl,
        defaultBranch: repository.defaultBranch || "main",
        installationId: repository.installationId,
        installationAccount: repository.installationAccount ?? repository.owner
      });
      setIntegrationDetail(await getIntegration(selectedIntegrationId));
      setRepositorySaved(`${repository.owner}/${repository.name} added.`);
      await refreshIntegrations();
    } catch (error) {
      setRepositoryError(error instanceof Error ? error.message : "Could not connect repository.");
    } finally {
      setAddingRepositoryUrl(undefined);
    }
  }

  async function handleRemoveRepository(repository: ConnectedRepository) {
    if (!selectedIntegrationId) {
      setRepositoryError("Select a GitHub integration before removing repositories.");
      return;
    }

    const confirmed = window.confirm(`Remove ${repository.owner}/${repository.name} from this integration?`);
    if (!confirmed) {
      return;
    }

    setRepositoryError(undefined);
    setRepositorySaved(undefined);
    setRemovingRepositoryId(repository.id);
    try {
      await deleteConnectedRepository(selectedIntegrationId, repository.id);
      setIntegrationDetail(await getIntegration(selectedIntegrationId));
      setRepositorySaved(`${repository.owner}/${repository.name} removed.`);
      await refreshIntegrations();
    } catch (error) {
      setRepositoryError(error instanceof Error ? error.message : "Could not remove repository.");
    } finally {
      setRemovingRepositoryId(undefined);
    }
  }

  function handleInstallGitHubApp() {
    if (!integrationDetail?.setupInstructions.installationUrl) {
      setRepositoryError("Select an integration with a discovered GitHub App installation URL.");
      return;
    }

    window.location.href = integrationDetail.setupInstructions.installationUrl;
  }

  async function handleIdentityToggle(enabled: boolean) {
    if (!selectedIntegrationId) {
      return;
    }
    if (enabled && !currentUser?.authenticated) {
      handleLogin(buildReturnUrl({ page: "integrations", integrationId: selectedIntegrationId, enableIdentityProviderId: selectedIntegrationId }));
      return;
    }

    setIntegrationError(undefined);
    setIntegrationSaved(undefined);
    try {
      setIntegrationDetail(await setIdentityProviderEnabled(selectedIntegrationId, enabled));
      setIntegrationSaved(enabled ? "Identity provider enabled." : "Identity provider disabled.");
      await refreshIntegrations();
    } catch (error) {
      setIntegrationError(error instanceof Error ? error.message : "Could not update identity provider.");
    }
  }

  async function handleIdentityRestart() {
    if (!selectedIntegrationId) {
      return;
    }

    setIntegrationError(undefined);
    setIntegrationSaved(undefined);
    setRestartingIdentityProvider(true);
    try {
      setIntegrationDetail(await restartIdentityProvider(selectedIntegrationId));
      setIntegrationSaved("Restart triggered. GitHub login will use this integration after the application starts again.");
      await refreshIntegrations();
    } catch (error) {
      setIntegrationError(error instanceof Error ? error.message : "Could not restart the application.");
    } finally {
      setRestartingIdentityProvider(false);
    }
  }

  function handleLogin(returnUrl?: string) {
    const target = returnUrl ?? window.location.pathname + window.location.search;
    window.location.href = `/api/auth/github/challenge?returnUrl=${encodeURIComponent(target)}`;
  }

  async function handleLogout() {
    setAuthBusy(true);
    setAuthError(undefined);
    try {
      await logout();
      setCurrentUser(current => ({
        authenticated: false,
        authorized: false,
        authRequired: current?.authRequired ?? false,
        canViewWorkflows: false,
        canTriggerWorkflows: false,
        canAdminister: false
      }));
      setInviteCode(undefined);
      setInviteLink(undefined);
    } catch (error) {
      setAuthError(error instanceof Error ? error.message : "Could not log out.");
    } finally {
      setAuthBusy(false);
    }
  }

  async function handleUpdateUserRoles(userId: string, roles: string[]) {
    setUpdatingManagementUserId(userId);
    setUserManagementError(undefined);
    setUserManagementSaved(undefined);
    try {
      const updated = await updateManagementUserRoles(userId, { roles });
      setManagementUsers(current => current.map(user => user.id === updated.id ? updated : user));
      setUserManagementSaved("User roles updated.");
      if (currentUser?.id === updated.id) {
        await refreshCurrentUser();
      }
    } catch (error) {
      setUserManagementError(error instanceof Error ? error.message : "Could not update user roles.");
    } finally {
      setUpdatingManagementUserId(undefined);
    }
  }

  async function handleCreateInvite() {
    setAuthBusy(true);
    setAuthError(undefined);
    try {
      const invite = await createInvite();
      setInviteCode(invite.code ?? undefined);
      setInviteLink(invite.code ? buildAbsoluteInviteLink(invite.code) : undefined);
    } catch (error) {
      setAuthError(error instanceof Error ? error.message : "Could not create invite.");
    } finally {
      setAuthBusy(false);
    }
  }

  async function handleRedeemInvite(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!redeemCode.trim()) {
      return;
    }

    setAuthBusy(true);
    setAuthError(undefined);
    try {
      await redeemInvite(redeemCode.trim());
      setRedeemCode("");
      replaceUrlParams({ page: "users", inviteRedeemed: "true" });
      await refreshCurrentUser();
    } catch (error) {
      setAuthError(error instanceof Error ? error.message : "Could not redeem invite.");
    } finally {
      setAuthBusy(false);
    }
  }

  function navigateToPage(page: Page) {
    navigate(pagePaths[page]);
    setMenuOpen(false);
  }

  function renderRefreshButton() {
    if (activePage === "workflows") {
      return (
        <button type="button" className="secondary-button" onClick={() => void refreshWorkflows()} disabled={loadingWorkflows}>
          {loadingWorkflows ? "Refreshing" : "Refresh"}
        </button>
      );
    }

    if (activePage === "workflow-definitions") {
      return (
        <button type="button" className="secondary-button" onClick={() => void refreshWorkflowDefinitions()} disabled={loadingWorkflowDefinitions}>
          {loadingWorkflowDefinitions ? "Refreshing" : "Refresh"}
        </button>
      );
    }

    if (activePage === "integrations") {
      return (
        <button type="button" className="secondary-button" onClick={() => void refreshIntegrations()} disabled={loadingIntegrations}>
          {loadingIntegrations ? "Refreshing" : "Refresh"}
        </button>
      );
    }

    if (activePage === "repositories") {
      return (
        <button type="button" className="secondary-button" onClick={() => void refreshAvailableRepositories()} disabled={loadingAvailableRepositories}>
          {loadingAvailableRepositories ? "Refreshing" : "Refresh"}
        </button>
      );
    }

    if (activePage === "users") {
      return (
        <button type="button" className="secondary-button" onClick={() => void refreshCurrentUser()} disabled={authBusy}>
          Refresh
        </button>
      );
    }

    return null;
  }

  if (!currentUser) {
    return (
      <main className="auth-gate-page">
        <section className="panel auth-gate-panel">
          <p className="eyebrow">Formicae</p>
          <h1>Loading Session</h1>
          <p className="muted">Checking whether this installation requires identity provider login.</p>
          {authError ? <p className="error-text">{authError}</p> : null}
        </section>
      </main>
    );
  }
  if (currentUser.authenticated && currentUser.authRequired && !currentUser.authorized) {
    return (
      <InviteCodePage
        authError={authError}
        redeemCode={redeemCode}
        busy={authBusy}
        setRedeemCode={setRedeemCode}
        onLogout={handleLogout}
        onRedeemInvite={handleRedeemInvite}
      />
    );
  }

  if (!currentUser.authenticated && currentUser.authRequired) {
    return (
      <main className="auth-gate-page">
        <section className="panel auth-gate-panel">
          <p className="eyebrow">Formicae</p>
          <h1>Redirecting to Login</h1>
          <p className="muted">This Formicae installation requires an identity provider login.</p>
          {authError ? <p className="error-text">{authError}</p> : null}
        </section>
      </main>
    );
  }

  function renderActivePage() {
    return activePage === "workflows" ? (
        <>
          <section className="workspace-grid">
        <div className="left-stack">
          <form className="panel trigger-panel" onSubmit={handleSubmit}>
            <div className="panel-heading">
              <h2>Manual Trigger</h2>
            </div>
            <label>
              <span>Issue URL</span>
              <input
                value={form.issueUrl}
                onChange={event => setForm(current => ({ ...current, issueUrl: event.target.value }))}
                placeholder="https://github.com/org/repo/issues/1"
                type="url"
              />
            </label>
            <label>
              <span>Repository URL</span>
              <input
                value={form.repositoryUrl}
                onChange={event => setForm(current => ({ ...current, repositoryUrl: event.target.value }))}
                placeholder="https://github.com/org/repo"
                type="url"
              />
            </label>
            <div className="form-row">
              <label>
                <span>Base Branch</span>
                <input
                  value={form.baseBranch}
                  onChange={event => setForm(current => ({ ...current, baseBranch: event.target.value }))}
                />
              </label>
              <label>
                <span>Model</span>
                <input
                  value={form.model}
                  onChange={event => {
                    modelTouched.current = true;
                    setForm(current => ({ ...current, model: event.target.value }));
                  }}
                  placeholder="optional"
                />
              </label>
            </div>
            <label>
              <span>Workflow Definition</span>
              <select
                value={form.workflowDefinitionVersionId}
                onChange={event => setForm(current => ({ ...current, workflowDefinitionVersionId: event.target.value }))}
                disabled={enabledDefinitionVersions.length === 0 || !canTriggerWorkflows}
              >
                {enabledDefinitionVersions.map(({ definition, version }) => (
                  <option key={version.id} value={version.id}>
                    {definition.name} v{version.version}{version.isDefault ? " (default)" : ""}
                  </option>
                ))}
              </select>
            </label>
            {enabledDefinitionVersions.length === 0 ? (
              <p className="muted">No enabled workflow definition versions are available.</p>
            ) : null}
            {workflowDefinitionError ? <p className="error-text">{workflowDefinitionError}</p> : null}
            {formError ? <p className="error-text">{formError}</p> : null}
            <button type="submit" className="primary-button" disabled={submitting || !canTriggerWorkflows || enabledDefinitionVersions.length === 0}>
              {submitting ? "Starting" : "Start Workflow"}
            </button>
          </form>
        </div>

        <section className="panel recent-panel">
          <div className="panel-heading">
            <h2>Recent Runs</h2>
            {listError ? <span className="error-text">{listError}</span> : null}
          </div>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Created</th>
                  <th>Status</th>
                  <th>Step</th>
                  <th>Issue</th>
                  <th>Pull Request</th>
                  <th>Failure</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                {workflows.map(workflow => (
                  <tr
                    key={workflow.workflowId}
                    className={workflow.workflowId === selectedWorkflowId ? "selected-row" : undefined}
                    onClick={() => setSelectedWorkflowId(workflow.workflowId)}
                  >
                    <td>{formatDate(workflow.createdAt)}</td>
                    <td><StatusBadge value={formatEnum(workflow.status, workflowStatuses)} /></td>
                    <td>{formatEnum(workflow.currentStep, workflowSteps)}</td>
                    <td><ExternalLink href={workflow.issueUrl}>{shortUrl(workflow.issueUrl)}</ExternalLink></td>
                    <td>{workflow.pullRequestUrl ? <ExternalLink href={workflow.pullRequestUrl}>Open</ExternalLink> : <span className="muted">None</span>}</td>
                    <td>{workflow.failureReason ? <span className="failure-cell">{workflow.failureReason}</span> : <span className="muted">None</span>}</td>
                    <td>
                      {formatEnum(workflow.status, workflowStatuses) === "Failed" ? (
                        <button
                          type="button"
                          className="secondary-button table-action-button"
                          onClick={event => {
                            event.stopPropagation();
                            void handleRetryWorkflow(workflow);
                          }}
                          disabled={retryingWorkflowId === workflow.workflowId || !canTriggerWorkflows}
                        >
                          {retryingWorkflowId === workflow.workflowId ? "Retrying" : "Retry"}
                        </button>
                      ) : null}
                    </td>
                  </tr>
                ))}
                {workflows.length === 0 ? (
                  <tr>
                    <td colSpan={7} className="empty-cell">{loadingWorkflows ? "Loading workflows" : "No workflows yet"}</td>
                  </tr>
                ) : null}
              </tbody>
            </table>
          </div>
        </section>
      </section>

      <section className="panel detail-panel">
        <div className="panel-heading">
          <h2>Workflow Detail</h2>
          {detail.loading ? <span className="muted">Loading</span> : null}
        </div>
        {selectedWorkflow ? (
          <div className="detail-grid">
            <div className="summary-list">
              <SummaryItem label="Workflow ID" value={selectedWorkflow.workflowId} mono />
              <SummaryItem label="Status" value={formatEnum(selectedWorkflow.status, workflowStatuses)} />
              <SummaryItem label="Current Step" value={formatEnum(selectedWorkflow.currentStep, workflowSteps)} />
              <SummaryItem label="Issue" value={<ExternalLink href={selectedWorkflow.issueUrl}>{selectedWorkflow.issueUrl}</ExternalLink>} />
              <SummaryItem label="Pull Request" value={selectedWorkflow.pullRequestUrl ? <ExternalLink href={selectedWorkflow.pullRequestUrl}>{selectedWorkflow.pullRequestUrl}</ExternalLink> : "None"} />
              <SummaryItem label="Failure" value={selectedWorkflow.failureReason ?? "None"} />
            </div>

            <div className="detail-stack">
              {detail.error ? <p className="error-text">{detail.error}</p> : null}
              {failureEvents.length > 0 ? (
                <section>
                  <h3>Failure Details</h3>
                  <div className="failure-detail-list">
                    {failureEvents.map(event => (
                      <article className="failure-detail" key={event.id}>
                        <div className="timeline-meta">
                          <time>{formatDate(event.createdAt)}</time>
                          <StatusBadge value={event.type} />
                        </div>
                        <p>{event.message}</p>
                        <Expandable title="Stack Trace" content={formatFailureDetails(event.detailsJson ?? "")} pre />
                      </article>
                    ))}
                  </div>
                </section>
              ) : null}
              {detail.signals.length > 0 ? (
                <section>
                  <h3>Signals</h3>
                  <div className="signal-list">
                    {detail.signals.map(signal => (
                      <div className={`signal-row signal-${signal.severity.toLowerCase()}`} key={`${signal.taskRunId ?? "workflow"}-${signal.reason}`}>
                        <strong>{signal.severity}</strong>
                        <span>{signal.reason}</span>
                        <time>{formatDate(signal.observedAt)}</time>
                      </div>
                    ))}
                  </div>
                </section>
              ) : null}

              <section>
                <h3>Timeline</h3>
                <div className="timeline-list">
                  {detail.events.map(event => (
                    <article className="timeline-item" key={event.id}>
                      <div className="timeline-meta">
                        <time>{formatDate(event.createdAt)}</time>
                        <StatusBadge value={event.type} />
                        <span>{event.level}</span>
                      </div>
                      <p>{event.message}</p>
                      {event.detailsJson ? <Expandable title="Event Details" content={formatJson(event.detailsJson)} pre /> : null}
                    </article>
                  ))}
                  {detail.events.length === 0 ? <p className="muted">No events recorded.</p> : null}
                </div>
              </section>

              <section>
                <h3>Task Runs</h3>
                <div className="run-list">
                  {detail.runs.map(run => {
                    const runStatus = formatEnum(run.status, taskRunStatuses);
                    const retrying = retryingRunId === run.id;
                    return (
                    <article className="run-card" key={run.id}>
                      <div className="run-meta">
                        <strong>{formatEnum(run.kind, taskRunKinds)}</strong>
                        <StatusBadge value={runStatus} />
                        <span>{formatDate(run.updatedAt)}</span>
                        <span>{formatDuration(run.startedAt, run.completedAt)}</span>
                        {runStatus === "Failed" ? (
                          <button
                            type="button"
                            className="secondary-button run-action-button"
                            onClick={() => void handleRetryRun(run)}
                            disabled={retrying || !canTriggerWorkflows}
                          >
                            {retrying ? "Retrying" : "Retry"}
                          </button>
                        ) : null}
                      </div>
                      {run.failureReason ? <p className="error-text">{run.failureReason}</p> : null}
                      {run.agentMessages.length > 0 ? (
                        <div className="agent-message-list">
                          {run.agentMessages.map(message => (
                            <Expandable
                              key={message.sequence}
                              title={`Agent Message ${message.sequence + 1}${message.role ? `: ${message.role}` : ""}`}
                              content={message.content}
                            />
                          ))}
                        </div>
                      ) : null}
                      {run.output ? <Expandable title="Raw Output" content={run.output} pre /> : <p className="muted">No output recorded.</p>}
                    </article>
                    );
                  })}
                  {detail.runs.length === 0 ? <p className="muted">No task runs recorded.</p> : null}
                </div>
              </section>

              <section>
                <h3>Chat Messages</h3>
                <div className="chat-list">
                  {detail.chatMessages.map(message => (
                    <article className="chat-row" key={message.id}>
                      <div className="chat-meta">
                        <strong>{message.author}</strong>
                        <time>{formatDate(message.updatedAt)}</time>
                        <ExternalLink href={message.url}>Open</ExternalLink>
                      </div>
                      <Expandable title="Message" content={message.body} />
                    </article>
                  ))}
                  {detail.chatMessages.length === 0 ? <p className="muted">No chat messages recorded.</p> : null}
                </div>
              </section>

              <section>
                <h3>Logs</h3>
                <div className="log-list">
                  {detail.logs.map(log => (
                    <div className="log-row" key={log.id}>
                      <time>{formatDate(log.createdAt)}</time>
                      <span>{log.level}</span>
                      <Expandable title="Log Message" content={log.message} />
                    </div>
                  ))}
                  {detail.logs.length === 0 ? <p className="muted">No logs recorded.</p> : null}
                </div>
              </section>
            </div>
          </div>
        ) : (
          <p className="muted">Select a workflow to inspect runs and logs.</p>
        )}
      </section>
        </>
      ) : activePage === "workflow-definitions" ? (
        <WorkflowDefinitionsPage
          definitions={workflowDefinitions}
          loading={loadingWorkflowDefinitions}
          error={workflowDefinitionError}
          saved={workflowDefinitionSaved}
          canAdminister={canAdminister}
          onRefresh={refreshWorkflowDefinitions}
          onSaved={message => {
            setWorkflowDefinitionSaved(message);
            setWorkflowDefinitionError(undefined);
          }}
          onError={message => {
            setWorkflowDefinitionError(message);
            setWorkflowDefinitionSaved(undefined);
          }}
        />
      ) : activePage === "integrations" ? (
        <IntegrationsPage
          integrations={integrations}
          integrationDetail={integrationDetail}
          selectedIntegrationId={selectedIntegrationId}
          integrationError={integrationError}
          integrationSaved={integrationSaved}
          githubIntegrationForm={githubIntegrationForm}
          creatingIntegration={creatingIntegration}
          deletingIntegration={deletingIntegration}
          restartingIdentityProvider={restartingIdentityProvider}
          setGitHubIntegrationForm={setGitHubIntegrationForm}
          onCreateIntegration={handleCreateIntegration}
          onSelectIntegration={handleSelectIntegration}
          onRotateWebhookSecret={handleRotateWebhookSecret}
          onDeleteIntegration={handleDeleteIntegration}
          onIdentityToggle={handleIdentityToggle}
          onIdentityRestart={handleIdentityRestart}
          canAdminister={canAdminister}
        />
      ) : activePage === "repositories" ? (
        <RepositoriesPage
          integrations={integrations}
          integrationDetail={integrationDetail}
          selectedIntegrationId={selectedIntegrationId}
          availableRepositories={availableRepositories}
          repositorySearch={repositorySearch}
          connectedRepositorySearch={connectedRepositorySearch}
          repositoryError={repositoryError}
          repositorySaved={repositorySaved}
          loadingAvailableRepositories={loadingAvailableRepositories}
          addingRepositoryUrl={addingRepositoryUrl}
          removingRepositoryId={removingRepositoryId}
          setRepositorySearch={setRepositorySearch}
          setConnectedRepositorySearch={setConnectedRepositorySearch}
          onSelectIntegration={handleSelectIntegration}
          onInstallGitHubApp={handleInstallGitHubApp}
          onAddRepository={handleAddRepository}
          onRemoveRepository={handleRemoveRepository}
          canAdminister={canAdminister}
        />
      ) : activePage === "users" ? (
        <UsersPage
          currentUser={currentUser}
          authError={authError}
          inviteCode={inviteCode}
          inviteLink={inviteLink}
          busy={authBusy}
          roles={managementRoles}
          users={managementUsers}
          loadingUsers={loadingManagementUsers}
          updatingUserId={updatingManagementUserId}
          managementError={userManagementError}
          managementSaved={userManagementSaved}
          canAdminister={canAdminister}
          onLogout={handleLogout}
          onCreateInvite={handleCreateInvite}
          onUpdateUserRoles={handleUpdateUserRoles}
        />
      ) : (
        <SettingsPage
          aiSettingsList={aiSettingsList}
          selectedAiSettings={selectedAiSettings}
          selectedAiSettingsId={selectedAiSettingsId}
          aiSettingsForm={aiSettingsForm}
          loadingAiSettings={loadingAiSettings}
          savingAiSettings={savingAiSettings}
          aiSettingsError={aiSettingsError}
          aiSettingsSaved={aiSettingsSaved}
          codexAuthConnection={codexAuthConnection}
          startingCodexAuth={startingCodexAuth}
          setAiSettingsForm={setAiSettingsForm}
          onSelectAiSettings={handleSelectAiSettings}
          onNewAiSettings={handleNewAiSettings}
          onStartCodexAuthConnection={handleStartCodexAuthConnection}
          onSubmit={handleAiSettingsSubmit}
          canAdminister={canAdminister}
        />
    );
  }

  return (
    <main className={`app-shell${menuOpen ? " menu-open" : ""}`}>
      <button
        type="button"
        className="drawer-backdrop"
        aria-label="Close navigation"
        onClick={() => setMenuOpen(false)}
      />
      <div className="app-layout">
        <aside className="side-nav" aria-label="Primary navigation">
          <div className="side-nav-brand">
            <p className="eyebrow">Formicae</p>
            <strong>Control</strong>
          </div>
          <nav className="side-nav-menu">
            {navigationItems.map(item => (
              <button
                type="button"
                className={`menu-button${activePage === item.page ? " active" : ""}`}
                onClick={() => navigateToPage(item.page)}
                disabled={item.disabled}
                key={item.page}
              >
                {item.label}
              </button>
            ))}
          </nav>
          <div className="side-nav-footer">
            <AccountStatus
              currentUser={currentUser}
              busy={authBusy}
              onLogout={handleLogout}
            />
            <span className="app-version">Formicae {appVersion ? `v${appVersion}` : "version loading"}</span>
          </div>
        </aside>
        <section className="app-content">
          <header className="content-header">
            <button
              type="button"
              className="mobile-menu-button"
              onClick={() => setMenuOpen(true)}
              aria-label="Open navigation"
              aria-expanded={menuOpen}
            >
              Menu
            </button>
            <div>
              <p className="eyebrow">Formicae</p>
              <h1>{pageTitle(activePage)}</h1>
            </div>
            <div className="content-header-actions">
              {renderRefreshButton()}
            </div>
          </header>
          {renderActivePage()}
        </section>
      </div>
    </main>
  );
}

function AccountStatus({
  currentUser,
  busy,
  onLogout
}: {
  currentUser?: CurrentUser;
  busy: boolean;
  onLogout: () => void;
}) {
  if (!currentUser?.authenticated) {
    return null;
  }

  return (
    <div className="account-status">
      <span className="auth-user">{currentUser.name ?? currentUser.email ?? currentUser.provider ?? "Signed in"}</span>
      <StatusBadge value={currentUser.authorized ? "Authorized" : "InviteRequired"} />
      <button type="button" className="secondary-button compact-button" onClick={onLogout} disabled={busy}>Logout</button>
    </div>
  );
}

function InviteCodePage({
  authError,
  redeemCode,
  busy,
  setRedeemCode,
  onLogout,
  onRedeemInvite
}: {
  authError?: string;
  redeemCode: string;
  busy: boolean;
  setRedeemCode: Dispatch<SetStateAction<string>>;
  onLogout: () => void;
  onRedeemInvite: (event: FormEvent<HTMLFormElement>) => void;
}) {
  return (
    <main className="auth-gate-page">
      <section className="panel auth-gate-panel">
        <p className="eyebrow">Formicae</p>
        <div className="panel-heading">
          <h1>Invite Code Required</h1>
          <StatusBadge value="InviteRequired" />
        </div>
        <p className="muted">Your identity provider login succeeded, but this Formicae installation only allows verified users. Ask an existing verified user to create an invite link, then open it or paste the invite code here.</p>
        <form onSubmit={onRedeemInvite} className="invite-form">
          <label>
            <span>Invite Code</span>
            <input value={redeemCode} onChange={event => setRedeemCode(event.target.value)} placeholder="Paste invite code" autoFocus />
          </label>
          <button type="submit" className="primary-button" disabled={busy || !redeemCode.trim()}>{busy ? "Redeeming" : "Redeem Invite"}</button>
        </form>
        {authError ? <p className="error-text">{authError}</p> : null}
        <button type="button" className="secondary-button" onClick={onLogout} disabled={busy}>Logout</button>
      </section>
    </main>
  );
}

function UsersPage({
  currentUser,
  authError,
  inviteCode,
  inviteLink,
  busy,
  roles,
  users,
  loadingUsers,
  updatingUserId,
  managementError,
  managementSaved,
  canAdminister,
  onLogout,
  onCreateInvite,
  onUpdateUserRoles
}: {
  currentUser?: CurrentUser;
  authError?: string;
  inviteCode?: string;
  inviteLink?: string;
  busy: boolean;
  roles: ManagementRole[];
  users: ManagementUser[];
  loadingUsers: boolean;
  updatingUserId?: string;
  managementError?: string;
  managementSaved?: string;
  canAdminister: boolean;
  onLogout: () => void;
  onCreateInvite: () => void;
  onUpdateUserRoles: (userId: string, roles: string[]) => void;
}) {
  return (
    <section className="users-page">
      <section className="user-management-layout">
        <div className="left-stack">
          <section className="panel users-panel">
            <div className="panel-heading">
              <h2>Current User</h2>
              {currentUser?.authenticated ? <StatusBadge value={currentUser.authorized ? "Authorized" : "InviteRequired"} /> : <StatusBadge value="Anonymous" />}
            </div>
            <div className="summary-list compact">
              <SummaryItem label="Status" value={currentUser?.authenticated ? "Signed in" : "Not signed in"} />
              <SummaryItem label="Name" value={currentUser?.name ?? "None"} />
              <SummaryItem label="Email" value={currentUser?.email ?? "None"} />
              <SummaryItem label="Provider" value={currentUser?.provider ?? "None"} />
            </div>
            <div className="permission-strip" aria-label="Current user permissions">
              <StatusBadge value={currentUser?.canViewWorkflows ? "WorkflowView" : "NoWorkflowView"} />
              <StatusBadge value={currentUser?.canTriggerWorkflows ? "WorkflowOperate" : "NoWorkflowOperate"} />
              <StatusBadge value={currentUser?.canAdminister ? "ManagementAdmin" : "NoManagementAdmin"} />
            </div>
            {currentUser?.authenticated ? (
              <div className="button-row">
                <button type="button" className="secondary-button" onClick={onLogout} disabled={busy}>Logout</button>
              </div>
            ) : null}
            {authError ? <p className="error-text">{authError}</p> : null}
          </section>

          <section className="panel users-panel">
            <div className="panel-heading">
              <h2>Invite Links</h2>
            </div>
            {canAdminister ? (
              <>
                <p className="muted">Create an invite link for another identity provider user. Redeeming an invite grants management admin access.</p>
                <button type="button" className="primary-button user-action-button" onClick={onCreateInvite} disabled={busy}>{busy ? "Creating" : "Create Invite Link"}</button>
                {inviteLink ? (
                  <div className="invite-result">
                    <label>
                      <span>Invite Link</span>
                      <input value={inviteLink} readOnly onFocus={event => event.currentTarget.select()} />
                    </label>
                    {inviteCode ? <code className="invite-code">{inviteCode}</code> : null}
                  </div>
                ) : null}
              </>
            ) : (
              <p className="muted">Management admins can create invite links.</p>
            )}
          </section>

          <section className="panel users-panel">
            <div className="panel-heading">
              <h2>Roles</h2>
            </div>
            <div className="role-reference-list">
              {roles.length > 0 ? roles.map(role => (
                <article className="role-reference" key={role.name}>
                  <div>
                    <strong>{formatRoleName(role.name)}</strong>
                    <p className="muted">{role.description}</p>
                  </div>
                  <PermissionChips permissions={role.permissions} />
                </article>
              )) : <p className="muted">Role definitions load for management admins.</p>}
            </div>
          </section>
        </div>

        <section className="panel users-directory-panel">
          <div className="panel-heading">
            <h2>User Directory</h2>
            {loadingUsers ? <span className="muted">Loading</span> : <StatusBadge value={`${users.length}`} />}
          </div>
          {managementError ? <p className="error-text">{managementError}</p> : null}
          {managementSaved ? <p className="success-text">{managementSaved}</p> : null}
          {canAdminister ? (
            <UserManagementTable
              currentUserId={currentUser?.id ?? undefined}
              roles={roles}
              users={users}
              updatingUserId={updatingUserId}
              onUpdateUserRoles={onUpdateUserRoles}
            />
          ) : (
            <p className="muted">Management admins can view users and assign roles.</p>
          )}
        </section>
      </section>
    </section>
  );
}

function UserManagementTable({
  currentUserId,
  roles,
  users,
  updatingUserId,
  onUpdateUserRoles
}: {
  currentUserId?: string;
  roles: ManagementRole[];
  users: ManagementUser[];
  updatingUserId?: string;
  onUpdateUserRoles: (userId: string, roles: string[]) => void;
}) {
  return (
    <div className="table-wrap user-table-wrap">
      <table className="user-management-table">
        <thead>
          <tr>
            <th>User</th>
            <th>Roles</th>
            <th>Permissions</th>
            <th>Last Login</th>
          </tr>
        </thead>
        <tbody>
          {users.map(user => (
            <tr key={user.id}>
              <td>
                <strong>{user.displayName ?? user.userName ?? user.email ?? "Unnamed user"}</strong>
                <span className="table-subtext">{user.email ?? user.userName ?? user.id}</span>
                <span className="table-subtext">{user.provider ?? "Local identity"}</span>
              </td>
              <td>
                <div className="role-toggle-grid">
                  {roles.map(role => {
                    const checked = user.roles.includes(role.name);
                    const selfAdminRole = user.id === currentUserId && role.name === "ManagementAdmin";
                    const nextRoles = checked
                      ? user.roles.filter(userRole => userRole !== role.name)
                      : [...user.roles, role.name];

                    return (
                      <label className="role-toggle" key={role.name} title={role.description}>
                        <input
                          type="checkbox"
                          checked={checked}
                          disabled={updatingUserId === user.id || (selfAdminRole && checked)}
                          onChange={() => onUpdateUserRoles(user.id, nextRoles)}
                        />
                        <span>{formatRoleName(role.name)}</span>
                      </label>
                    );
                  })}
                </div>
              </td>
              <td><PermissionChips permissions={user.permissions} /></td>
              <td>{user.lastLoginAt ? formatDate(user.lastLoginAt) : <span className="muted">Never</span>}</td>
            </tr>
          ))}
          {users.length === 0 ? (
            <tr>
              <td colSpan={4} className="empty-cell">No users found.</td>
            </tr>
          ) : null}
        </tbody>
      </table>
    </div>
  );
}

function PermissionChips({ permissions }: { permissions: string[] }) {
  return permissions.length > 0 ? (
    <div className="permission-chip-list">
      {permissions.map(permission => <span className="permission-chip" key={permission}>{permission}</span>)}
    </div>
  ) : <span className="muted">No permissions</span>;
}
function IntegrationsPage({
  integrations,
  integrationDetail,
  selectedIntegrationId,
  integrationError,
  integrationSaved,
  githubIntegrationForm,
  creatingIntegration,
  deletingIntegration,
  restartingIdentityProvider,
  setGitHubIntegrationForm,
  onCreateIntegration,
  onSelectIntegration,
  onRotateWebhookSecret,
  onDeleteIntegration,
  onIdentityToggle,
  onIdentityRestart,
  canAdminister
}: {
  integrations: IntegrationSummary[];
  integrationDetail?: IntegrationDetail;
  selectedIntegrationId?: string;
  integrationError?: string;
  integrationSaved?: string;
  githubIntegrationForm: GitHubIntegrationFormState;
  creatingIntegration: boolean;
  deletingIntegration: boolean;
  restartingIdentityProvider: boolean;
  setGitHubIntegrationForm: Dispatch<SetStateAction<GitHubIntegrationFormState>>;
  onCreateIntegration: (event: FormEvent<HTMLFormElement>) => void;
  onSelectIntegration: (integrationId: string) => void;
  onRotateWebhookSecret: () => void;
  onDeleteIntegration: () => void;
  onIdentityToggle: (enabled: boolean) => void;
  onIdentityRestart: () => void;
  canAdminister: boolean;
}) {
  return (
    <section className="integrations-page">
      <section className="workspace-grid">
        <div className="left-stack">
          <form className="panel" onSubmit={onCreateIntegration}>
            <div className="panel-heading">
              <h2>GitHub App</h2>
            </div>
            <label>
              <span>Display Name</span>
              <input
                value={githubIntegrationForm.displayName}
                onChange={event => setGitHubIntegrationForm(current => ({ ...current, displayName: event.target.value }))}
              />
            </label>
            <label>
              <span>Client ID</span>
              <input
                value={githubIntegrationForm.clientId}
                onChange={event => setGitHubIntegrationForm(current => ({ ...current, clientId: event.target.value }))}
              />
            </label>
            <label>
              <span>Client Secret Reference</span>
              <input
                value={githubIntegrationForm.clientSecretReference}
                onChange={event => setGitHubIntegrationForm(current => ({ ...current, clientSecretReference: event.target.value }))}
                placeholder="Optional, required only for identity provider login"
              />
            </label>
            <label>
              <span>Private Key PEM</span>
              <textarea
                rows={8}
                value={githubIntegrationForm.privateKey}
                onChange={event => setGitHubIntegrationForm(current => ({ ...current, privateKey: event.target.value }))}
                placeholder="-----BEGIN RSA PRIVATE KEY-----"
              />
            </label>
            <button type="submit" className="primary-button" disabled={creatingIntegration || !canAdminister}>
              {creatingIntegration ? "Creating" : "Create Integration"}
            </button>
          </form>

          <section className="panel">
            <div className="panel-heading">
              <h2>Configured</h2>
            </div>
            <div className="integration-list">
              {integrations.map(integration => (
                <button
                  type="button"
                  className={`integration-row${integration.id === selectedIntegrationId ? " selected" : ""}`}
                  key={integration.id}
                  onClick={() => onSelectIntegration(integration.id)}
                >
                  <strong>{integration.displayName}</strong>
                  <span>{integration.providerType}</span>
                </button>
              ))}
              {integrations.length === 0 ? <p className="muted">No integrations configured.</p> : null}
            </div>
          </section>
        </div>

        <section className="panel">
          <div className="panel-heading">
            <h2>Integration Detail</h2>
            {integrationDetail ? <StatusBadge value={integrationDetail.identityProviderEnabled ? "IdentityOn" : "IdentityOff"} /> : null}
          </div>
          {integrationError ? <p className="error-text">{integrationError}</p> : null}
          {integrationSaved ? <p className="success-text">{integrationSaved}</p> : null}

          {integrationDetail ? (
            <div className="detail-stack">
              <div className="summary-list compact">
                <SummaryItem label="Client ID" value={integrationDetail.gitHubAppClientId} mono />
                <SummaryItem label="App Slug" value={integrationDetail.gitHubAppSlug ?? "Not discovered"} mono />
                <SummaryItem label="Install URL" value={integrationDetail.setupInstructions.installationUrl || "Not available"} mono />
                <SummaryItem label="Webhook URL" value={integrationDetail.webhookUrl} mono />
                <SummaryItem label="Webhook Secret" value={integrationDetail.webhookSecret} mono />
                <SummaryItem label="OAuth Callback URL" value={integrationDetail.setupInstructions.callbackUrl} mono />
                <SummaryItem label="Setup Callback URL" value={integrationDetail.setupInstructions.installationCallbackUrl} mono />
              </div>

              <div className="button-row">
                <button type="button" className="secondary-button" onClick={onRotateWebhookSecret} disabled={!canAdminister}>Rotate Webhook Secret</button>
                <button type="button" className="secondary-button danger-button" onClick={onDeleteIntegration} disabled={deletingIntegration || !canAdminister}>
                  {deletingIntegration ? "Removing" : "Remove Integration"}
                </button>
              </div>

              <section className="settings-section">
                <h3>Identity Provider</h3>
                <label className="toggle-label">
                  <input
                    type="checkbox"
                    checked={integrationDetail.identityProviderEnabled}
                    onChange={event => onIdentityToggle(event.target.checked)}
                    disabled={!canAdminister && integrationDetail.identityProviderEnabled}
                  />
                  <span>Use as identity provider</span>
                </label>
                {integrationDetail.requiresRestart ? (
                  <div className="warning-actions">
                    <p className="warning-text">Restart required before GitHub login uses this integration.</p>
                    <button
                      type="button"
                      className="secondary-button"
                      onClick={onIdentityRestart}
                      disabled={restartingIdentityProvider || !canAdminister}
                    >
                      {restartingIdentityProvider ? "Restarting" : "Restart"}
                    </button>
                  </div>
                ) : null}
              </section>

              <section>
                <h3>GitHub App Setup</h3>
                <div className="checklist-grid">
                  <div>
                    <h4>Repository permissions</h4>
                    <ul>
                      {integrationDetail.setupInstructions.requiredRepositoryPermissions.map(permission => <li key={permission}>{permission}</li>)}
                    </ul>
                  </div>
                  <div>
                    <h4>Webhook events</h4>
                    <ul>
                      {integrationDetail.setupInstructions.requiredWebhookEvents.map(eventName => <li key={eventName}>{eventName}</li>)}
                    </ul>
                  </div>
                </div>
              </section>
            </div>
          ) : (
            <p className="muted">Create or select an integration.</p>
          )}
        </section>
      </section>
    </section>
  );
}

function RepositoriesPage({
  integrations,
  integrationDetail,
  selectedIntegrationId,
  availableRepositories,
  repositorySearch,
  connectedRepositorySearch,
  repositoryError,
  repositorySaved,
  loadingAvailableRepositories,
  addingRepositoryUrl,
  removingRepositoryId,
  setRepositorySearch,
  setConnectedRepositorySearch,
  onSelectIntegration,
  onInstallGitHubApp,
  onAddRepository,
  onRemoveRepository,
  canAdminister
}: {
  integrations: IntegrationSummary[];
  integrationDetail?: IntegrationDetail;
  selectedIntegrationId?: string;
  availableRepositories: GitHubUserRepository[];
  repositorySearch: string;
  connectedRepositorySearch: string;
  repositoryError?: string;
  repositorySaved?: string;
  loadingAvailableRepositories: boolean;
  addingRepositoryUrl?: string;
  removingRepositoryId?: string;
  setRepositorySearch: Dispatch<SetStateAction<string>>;
  setConnectedRepositorySearch: Dispatch<SetStateAction<string>>;
  onSelectIntegration: (integrationId: string) => void;
  onInstallGitHubApp: () => void;
  onAddRepository: (repository: GitHubUserRepository) => void;
  onRemoveRepository: (repository: ConnectedRepository) => void;
  canAdminister: boolean;
}) {
  const connectedUrls = new Set((integrationDetail?.repositories ?? []).map(repository => repository.repositoryUrl.toLowerCase()));
  const available = availableRepositories.filter(repository => matchesRepository(repository, repositorySearch));
  const connected = (integrationDetail?.repositories ?? []).filter(repository => matchesRepository(repository, connectedRepositorySearch));

  return (
    <section className="repositories-page">
      <section className="repository-toolbar panel">
        <label>
          <span>Integration</span>
          <select value={selectedIntegrationId ?? ""} onChange={event => onSelectIntegration(event.target.value)}>
            <option value="" disabled>Select integration</option>
            {integrations.map(integration => <option key={integration.id} value={integration.id}>{integration.displayName}</option>)}
          </select>
        </label>
        <button type="button" className="secondary-button" onClick={onInstallGitHubApp} disabled={!integrationDetail?.setupInstructions.installationUrl || !canAdminister}>Install GitHub App</button>
      </section>

      {repositoryError ? <p className="error-text repository-message">{repositoryError}</p> : null}
      {repositorySaved ? <p className="success-text repository-message">{repositorySaved}</p> : null}

      <section className="repository-management-grid">
        <section className="panel">
          <div className="panel-heading">
            <h2>Available Repositories</h2>
            {loadingAvailableRepositories ? <span className="muted">Loading</span> : null}
          </div>
          <label>
            <span>Search</span>
            <input value={repositorySearch} onChange={event => setRepositorySearch(event.target.value)} placeholder="owner, repository, or URL" />
          </label>
          <div className="repository-list">
            {available.map(repository => {
              const alreadyConnected = connectedUrls.has(repository.repositoryUrl.toLowerCase());
              return (
                <RepositoryCandidateRow
                  repository={repository}
                  alreadyConnected={alreadyConnected}
                  adding={addingRepositoryUrl === repository.repositoryUrl}
                  canAdminister={canAdminister}
                  onAdd={() => onAddRepository(repository)}
                  key={repository.repositoryUrl}
                />
              );
            })}
            {available.length === 0 ? <p className="muted">No repositories available. Install or grant the GitHub App access, then refresh the repository list.</p> : null}
          </div>
        </section>

        <section className="panel">
          <div className="panel-heading">
            <h2>Added Repositories</h2>
            <StatusBadge value={`${connected.length}`} />
          </div>
          <label>
            <span>Search</span>
            <input value={connectedRepositorySearch} onChange={event => setConnectedRepositorySearch(event.target.value)} placeholder="owner, repository, or URL" />
          </label>
          <div className="repository-list">
            {connected.map(repository => (
              <RepositoryRow
                repository={repository}
                removing={removingRepositoryId === repository.id}
                canAdminister={canAdminister}
                onRemove={() => onRemoveRepository(repository)}
                key={repository.id}
              />
            ))}
            {connected.length === 0 ? <p className="muted">No repositories added for this integration.</p> : null}
          </div>
        </section>
      </section>
    </section>
  );
}

function RepositoryCandidateRow({
  repository,
  alreadyConnected,
  adding,
  canAdminister,
  onAdd
}: {
  repository: GitHubUserRepository;
  alreadyConnected: boolean;
  adding: boolean;
  canAdminister: boolean;
  onAdd: () => void;
}) {
  return (
    <div className="repository-row">
      <div>
        <strong>{repository.owner}/{repository.name}</strong>
        <span>{repository.defaultBranch} {repository.private ? "private" : "public"}</span>
      </div>
      {alreadyConnected ? (
        <StatusBadge value="Added" />
      ) : (
        <button type="button" className="secondary-button" onClick={onAdd} disabled={adding || !canAdminister}>{adding ? "Adding" : "Add"}</button>
      )}
    </div>
  );
}

function matchesRepository(repository: { owner: string; name: string; repositoryUrl: string }, query: string) {
  const normalized = query.trim().toLowerCase();
  if (!normalized) {
    return true;
  }

  return `${repository.owner}/${repository.name} ${repository.repositoryUrl}`.toLowerCase().includes(normalized);
}

function RepositoryRow({
  repository,
  removing,
  canAdminister,
  onRemove
}: {
  repository: ConnectedRepository;
  removing: boolean;
  canAdminister: boolean;
  onRemove: () => void;
}) {
  return (
    <div className="repository-row">
      <div>
        <strong>{repository.owner}/{repository.name}</strong>
        <span>{repository.defaultBranch}</span>
      </div>
      <div className="repository-actions">
        <ExternalLink href={repository.repositoryUrl}>Open</ExternalLink>
        <button type="button" className="secondary-button danger-button" onClick={onRemove} disabled={removing || !canAdminister}>
          {removing ? "Removing" : "Remove"}
        </button>
      </div>
    </div>
  );
}

function SettingsPage({
  aiSettingsList,
  selectedAiSettings,
  selectedAiSettingsId,
  aiSettingsForm,
  loadingAiSettings,
  savingAiSettings,
  aiSettingsError,
  aiSettingsSaved,
  codexAuthConnection,
  startingCodexAuth,
  setAiSettingsForm,
  onSelectAiSettings,
  onNewAiSettings,
  onStartCodexAuthConnection,
  onSubmit,
  canAdminister
}: {
  aiSettingsList: AiSettings[];
  selectedAiSettings?: AiSettings;
  selectedAiSettingsId?: string;
  aiSettingsForm: AiSettingsFormState;
  loadingAiSettings: boolean;
  savingAiSettings: boolean;
  aiSettingsError?: string;
  aiSettingsSaved?: string;
  codexAuthConnection?: CodexAuthSetupStatus;
  startingCodexAuth: boolean;
  setAiSettingsForm: Dispatch<SetStateAction<AiSettingsFormState>>;
  onSelectAiSettings: (settingsId: string) => void;
  onNewAiSettings: () => void;
  onStartCodexAuthConnection: () => void;
  onSubmit: (event: FormEvent<HTMLFormElement>) => void;
  canAdminister: boolean;
}) {
  const apiKeyConfigured = selectedAiSettings?.hasApiKey || selectedAiSettings?.hasApiKeySecret;
  const subscriptionConfigured = selectedAiSettings?.hasSubscriptionAuth;
  const authOutput = stripAnsi(codexAuthConnection?.output ?? "");
  const loginSucceeded = codexAuthConnection?.status === "Succeeded";
  const [deviceCodeCopied, setDeviceCodeCopied] = useState(false);
  useEffect(() => setDeviceCodeCopied(false), [codexAuthConnection?.deviceLoginCode]);
  async function handleCopyDeviceCode(value: string) {
    await copyText(value);
    setDeviceCodeCopied(true);
    window.setTimeout(() => setDeviceCodeCopied(false), 1800);
  }

  return (
    <section className="settings-page ai-setup-page">
      <section className="panel settings-panel">
        <div className="panel-heading">
          <div>
            <h2>AI Setup</h2>
            <p className="muted setup-note">Create one or more AI configurations. Workflow agents use the first configured AI for now.</p>
          </div>
          {loadingAiSettings ? <span className="muted">Loading</span> : null}
        </div>
        <div className="ai-setup-layout">
          <aside className="ai-profile-list" aria-label="Configured AIs">
            <div className="section-heading-row">
              <h3>1. Choose AI</h3>
              <button type="button" className="secondary-button compact-button" onClick={onNewAiSettings} disabled={!canAdminister}>New</button>
            </div>
            <div className="ai-profile-buttons">
              {aiSettingsList.map((settings, index) => (
                <button
                  type="button"
                  className={`ai-profile-button${settings.id === selectedAiSettingsId ? " active" : ""}`}
                  key={settings.id}
                  onClick={() => onSelectAiSettings(settings.id)}
                >
                  <strong>{settings.name}</strong>
                  <span>{index === 0 ? "Used by agents" : settings.model || settings.agentKind}</span>
                </button>
              ))}
              {aiSettingsList.length === 0 ? <p className="muted">No AI configured yet.</p> : null}
            </div>
          </aside>
          <form onSubmit={onSubmit} className="ai-setup-form">
            <div className="settings-section">
              <h3>2. Name and runtime</h3>
              <label>
                <span>AI Name</span>
                <input value={aiSettingsForm.name} onChange={event => setAiSettingsForm(current => ({ ...current, name: event.target.value }))} placeholder="Production Codex" />
              </label>
              <div className="form-row">
                <label>
                  <span>Agent Type</span>
                  <select value={aiSettingsForm.agentKind} onChange={event => setAiSettingsForm(current => ({ ...current, agentKind: event.target.value }))}>
                    <option value="OpenHands">OpenHands</option>
                    <option value="Acp">ACP agent</option>
                  </select>
                </label>
                {aiSettingsForm.agentKind === "Acp" ? (
                  <label>
                    <span>ACP Agent</span>
                    <select value={aiSettingsForm.acpProvider} onChange={event => setAiSettingsForm(current => ({ ...current, acpProvider: event.target.value }))}>
                      <option value="ClaudeCode">Claude Code</option>
                      <option value="Codex">Codex</option>
                      <option value="GeminiCli">Gemini CLI</option>
                      <option value="Custom">Custom</option>
                    </select>
                  </label>
                ) : (
                  <label>
                    <span>Provider</span>
                    <input value={aiSettingsForm.provider} onChange={event => setAiSettingsForm(current => ({ ...current, provider: event.target.value }))} placeholder="OpenAI, Anthropic, OpenHands Cloud" />
                  </label>
                )}
              </div>
              <label>
                <span>Default Model</span>
                <input value={aiSettingsForm.model} onChange={event => setAiSettingsForm(current => ({ ...current, model: event.target.value }))} placeholder="Model used when a workflow does not override it" />
              </label>
            </div>
            <div className="settings-section">
              <h3>3. Add credentials</h3>
              <label>
                <span>Auth Method</span>
                <select value={aiSettingsForm.authMethod} onChange={event => setAiSettingsForm(current => ({ ...current, authMethod: event.target.value }))}>
                  <option value="ApiKey">API key</option>
                  <option value="OpenHandsCloud">OpenHands Cloud API key</option>
                  <option value="CodexSubscription">Subscription credentials</option>
                </select>
              </label>
              {aiSettingsForm.authMethod === "CodexSubscription" ? (
                <>
                  <div className="auth-setup-box">
                    <div className="secret-status"><span>Subscription credentials</span><StatusBadge value={subscriptionConfigured ? "Configured" : "NotConfigured"} /></div>
                    <div className="button-row">
                      <button type="button" className="secondary-button" onClick={onStartCodexAuthConnection} disabled={startingCodexAuth || !canAdminister || !selectedAiSettings}>
                        {startingCodexAuth ? "Starting" : subscriptionConfigured ? "Reconnect Codex" : "Connect Codex"}
                      </button>
                      {!selectedAiSettings ? <span className="muted">Save this AI before connecting.</span> : null}
                    </div>
                    {codexAuthConnection ? (
                      <div className="auth-output-block">
                        {loginSucceeded ? (
                          <div className="auth-success-card">
                            <strong>Codex login succeeded.</strong>
                            <span>Subscription credentials are connected and ready for agent runs.</span>
                          </div>
                        ) : (
                          <>
                            <div className="secret-status"><span>Login job</span><StatusBadge value={codexAuthConnection.status} /></div>
                            {codexAuthConnection.failureReason ? <p className="error-text">{codexAuthConnection.failureReason}</p> : null}
                            {codexAuthConnection.deviceLoginUrl || codexAuthConnection.deviceLoginCode ? (
                              <div className="device-login-card">
                                {codexAuthConnection.deviceLoginUrl ? (
                                  <div className="device-login-row">
                                    <span>Open</span>
                                    <a href={codexAuthConnection.deviceLoginUrl} target="_blank" rel="noreferrer">{codexAuthConnection.deviceLoginUrl}</a>
                                  </div>
                                ) : null}
                                {codexAuthConnection.deviceLoginCode ? (
                                  <div className="device-login-row">
                                    <span>Code</span>
                                    <code>{codexAuthConnection.deviceLoginCode}</code>
                                    <button type="button" className={`secondary-button compact-button${deviceCodeCopied ? " copied-button" : ""}`} onClick={() => handleCopyDeviceCode(codexAuthConnection.deviceLoginCode!)}>
                                      {deviceCodeCopied ? "Copied" : "Copy"}
                                    </button>
                                  </div>
                                ) : null}
                              </div>
                            ) : null}
                            <pre className="auth-output">{authOutput || "Waiting for login output..."}</pre>
                          </>
                        )}
                      </div>
                    ) : null}
                  </div>
                  <details className="manual-credentials">
                    <summary>Paste credential JSON manually</summary>
                    <label>
                      <span>Credential JSON</span>
                      <textarea value={aiSettingsForm.subscriptionCredentialJson} onChange={event => setAiSettingsForm(current => ({ ...current, subscriptionCredentialJson: event.target.value }))} placeholder={subscriptionConfigured ? "Configured. Leave blank to keep existing credentials." : "Paste subscription credential JSON"} rows={6} />
                    </label>
                  </details>
                </>
              ) : (
                <>
                  <label>
                    <span>API Key</span>
                    <input value={aiSettingsForm.llmApiKey} onChange={event => setAiSettingsForm(current => ({ ...current, llmApiKey: event.target.value }))} placeholder={apiKeyConfigured ? "Configured. Leave blank to keep existing key." : "Paste API key"} type="password" />
                  </label>
                  <div className="secret-status"><span>API key</span><StatusBadge value={apiKeyConfigured ? "Configured" : "NotConfigured"} /></div>
                </>
              )}
            </div>
            <details className="settings-section optional-settings">
              <summary>Optional settings</summary>
              {aiSettingsForm.agentKind === "Acp" ? (
                <label>
                  <span>ACP Command</span>
                  <input value={aiSettingsForm.acpCommand} onChange={event => setAiSettingsForm(current => ({ ...current, acpCommand: event.target.value }))} placeholder="custom stdio ACP server command" />
                </label>
              ) : null}
              <label>
                <span>Endpoint / Base URL</span>
                <input value={aiSettingsForm.endpointUrl} onChange={event => setAiSettingsForm(current => ({ ...current, endpointUrl: event.target.value }))} placeholder="https://api.example.com/v1" type="url" />
              </label>
              {aiSettingsForm.authMethod === "CodexSubscription" ? (
                <div className="form-row">
                  <label>
                    <span>Credential File</span>
                    <input value={aiSettingsForm.subscriptionCredentialFileName} onChange={event => setAiSettingsForm(current => ({ ...current, subscriptionCredentialFileName: event.target.value }))} placeholder="auth.json" />
                  </label>
                  <label>
                    <span>Credential Directory</span>
                    <input value={aiSettingsForm.subscriptionCredentialMountPath} onChange={event => setAiSettingsForm(current => ({ ...current, subscriptionCredentialMountPath: event.target.value }))} placeholder="/root/.codex" />
                  </label>
                </div>
              ) : (
                <div className="form-row">
                  <label>
                    <span>API Key Env Var</span>
                    <input value={aiSettingsForm.apiKeyEnvironmentVariable} onChange={event => setAiSettingsForm(current => ({ ...current, apiKeyEnvironmentVariable: event.target.value }))} placeholder="LLM_API_KEY" />
                  </label>
                  <label>
                    <span>Existing Secret Name</span>
                    <input value={aiSettingsForm.llmApiKeySecretName} onChange={event => setAiSettingsForm(current => ({ ...current, llmApiKeySecretName: event.target.value }))} placeholder="optional Kubernetes secret name" />
                  </label>
                </div>
              )}
            </details>
            {aiSettingsError ? <p className="error-text">{aiSettingsError}</p> : null}
            {aiSettingsSaved ? <p className="success-text">{aiSettingsSaved}</p> : null}
            <button type="submit" className="primary-button" disabled={savingAiSettings || !canAdminister}>
              {savingAiSettings ? "Saving" : "Save AI"}
            </button>
          </form>
        </div>
      </section>
    </section>
  );
}
function pageTitle(page: Page) {
  switch (page) {
    case "workflows":
      return "Workflow Management";
    case "workflow-definitions":
      return "Workflow Definitions";
    case "integrations":
      return "Integrations";
    case "repositories":
      return "Repositories";
    case "users":
      return "Users";
    case "settings":
      return "Settings";
  }
}

function parsePage(value: string | null): Page | undefined {
  return pages.includes(value as Page) ? (value as Page) : undefined;
}

function parsePagePath(pathname: string): Page | undefined {
  const entry = Object.entries(pagePaths).find(([, path]) => pathname === path || pathname === `${path}/`);
  return entry?.[0] as Page | undefined;
}

function buildReturnUrl(values: Record<string, string | undefined>) {
  const page = parsePage(values.page ?? null) ?? "workflows";
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) {
    if (key !== "page" && value) {
      params.set(key, value);
    }
  }

  const query = params.toString();
  return query ? `${pagePaths[page]}?${query}` : pagePaths[page];
}

function formatRoleName(role: string) {
  return role.replace(/([a-z])([A-Z])/g, "$1 $2");
}

function buildAbsoluteInviteLink(code: string) {
  return `${window.location.origin}${buildReturnUrl({ page: "users", invite: code })}`;
}

function toAiSettingsForm(settings: AiSettings): AiSettingsFormState {
  return {
    name: settings.name ?? "New AI",
    provider: settings.provider ?? "",
    model: settings.model ?? "",
    endpointUrl: settings.endpointUrl ?? "",
    agentKind: settings.agentKind ?? "OpenHands",
    acpProvider: settings.acpProvider ?? "ClaudeCode",
    acpCommand: settings.acpCommand ?? "",
    authMethod: settings.authMethod,
    llmApiKeySecretName: settings.llmApiKeySecretName ?? "",
    llmApiKey: "",
    apiKeyEnvironmentVariable: settings.apiKeyEnvironmentVariable ?? "",
    subscriptionCredentialJson: "",
    subscriptionCredentialFileName: settings.subscriptionCredentialFileName ?? "",
    subscriptionCredentialMountPath: settings.subscriptionCredentialMountPath ?? ""
  };
}

function createAiSettingsId() {
  return typeof crypto !== "undefined" && "randomUUID" in crypto
    ? crypto.randomUUID()
    : `ai-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

function SummaryItem({ label, value, mono }: { label: string; value: ReactNode; mono?: boolean }) {
  return (
    <div className="summary-item">
      <dt>{label}</dt>
      <dd className={mono ? "mono" : undefined}>{value}</dd>
    </div>
  );
}

function StatusBadge({ value }: { value: string }) {
  return <span className={`status-badge status-${value.toLowerCase()}`}>{value}</span>;
}

function ExternalLink({ href, children }: { href: string; children: ReactNode }) {
  return <a href={href} target="_blank" rel="noreferrer">{children}</a>;
}

function Expandable({ title, content, pre }: { title: string; content: string; pre?: boolean }) {
  const [expanded, setExpanded] = useState(false);
  return (
    <div className="expandable">
      <button type="button" className="expand-button" onClick={() => setExpanded(current => !current)}>
        {expanded ? "Collapse" : "Expand"} {title}
      </button>
      {pre ? (
        <pre className={expanded ? "expanded-content" : "collapsed-content"}>{content}</pre>
      ) : (
        <p className={expanded ? "expanded-content prose-content" : "collapsed-content prose-content"}>{content}</p>
      )}
    </div>
  );
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "short",
    timeStyle: "medium"
  }).format(new Date(value));
}

function formatEnum(value: string | number, labels: string[]) {
  if (typeof value === "number") {
    return labels[value] ?? String(value);
  }

  return value;
}

function formatDuration(startedAt?: string | null, completedAt?: string | null) {
  if (!startedAt) {
    return "Not started";
  }

  const start = new Date(startedAt).getTime();
  const end = completedAt ? new Date(completedAt).getTime() : Date.now();
  if (!Number.isFinite(start) || !Number.isFinite(end) || end < start) {
    return "Duration unavailable";
  }

  const seconds = Math.round((end - start) / 1000);
  if (seconds < 60) {
    return `${seconds}s`;
  }

  const minutes = Math.floor(seconds / 60);
  const remainingSeconds = seconds % 60;
  if (minutes < 60) {
    return `${minutes}m ${remainingSeconds}s`;
  }

  const hours = Math.floor(minutes / 60);
  return `${hours}h ${minutes % 60}m`;
}

function stripAnsi(value: string) {
  return value.replace(/\u001b\[[0-?]*[ -/]*[@-~]/g, "");
}

async function copyText(value: string) {
  await navigator.clipboard?.writeText(value);
}
function formatJson(value: string) {
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

function formatFailureDetails(value: string) {
  try {
    const payload = JSON.parse(value) as { stackTrace?: unknown; StackTrace?: unknown };
    const stackTrace = typeof payload.stackTrace === "string" ? payload.stackTrace : typeof payload.StackTrace === "string" ? payload.StackTrace : undefined;
    if (stackTrace?.trim()) {
      return stackTrace;
    }

    return JSON.stringify(payload, null, 2);
  } catch {
    return value;
  }
}

function shortUrl(value: string) {
  try {
    const url = new URL(value);
    return `${url.hostname}${url.pathname}`;
  } catch {
    return value;
  }
}
