import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useState } from "react";
import { BrowserRouter, Link, Navigate, Route, Routes, useNavigate, useParams } from "react-router-dom";
import { LoginPage } from "./features/auth/LoginPage";
import { RequireSession } from "./features/auth/RequireSession";
import { useSession } from "./features/auth/useSession";
import { TaskDetailPage } from "./features/tasks/TaskDetailPage";
import { TaskListPage } from "./features/tasks/TaskListPage";

const queryClient = new QueryClient();

function LoginRoute() {
  const { login } = useSession();
  const navigate = useNavigate();

  async function handleLogin(loginName: string, password: string): Promise<void> {
    await login(loginName, password);
    navigate("/", { replace: true });
  }

  return <LoginPage onLogin={handleLogin} />;
}

function HomePage() {
  const { session, logout } = useSession();
  const navigate = useNavigate();

  async function handleLogout() {
    await logout();
    navigate("/login", { replace: true });
  }

  return (
    <main>
      <h1>Orchestrator admin</h1>
      <p>Signed in as {session?.loginName}.</p>
      <p>
        <Link to="/tasks">View tasks</Link>
      </p>
      <button type="button" onClick={handleLogout}>
        Sign out
      </button>
    </main>
  );
}

function TaskDetailRoute() {
  const { taskId } = useParams<{ taskId: string }>();
  if (taskId === undefined) {
    return <Navigate to="/tasks" replace />;
  }
  return <TaskDetailPage taskId={taskId} />;
}

export function App() {
  const [client] = useState(() => queryClient);
  return (
    <QueryClientProvider client={client}>
      <BrowserRouter>
        <Routes>
          <Route path="/login" element={<LoginRoute />} />
          <Route element={<RequireSession />}>
            <Route path="/" element={<HomePage />} />
            <Route path="/tasks" element={<TaskListPage />} />
            <Route path="/tasks/:taskId" element={<TaskDetailRoute />} />
          </Route>
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
