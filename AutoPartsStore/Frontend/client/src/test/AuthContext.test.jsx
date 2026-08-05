import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, test, vi } from 'vitest';
import axios from 'axios';
import { AuthProvider, useAuth } from '../context/AuthContext';

vi.mock('axios', () => ({
  default: {
    defaults: { headers: { common: {} } },
    get: vi.fn(),
    post: vi.fn(),
  },
}));

function AuthHarness() {
  const { user, token, loading, login, logout, isAdmin } = useAuth();
  return (
    <div>
      <output data-testid="loading">{String(loading)}</output>
      <output data-testid="role">{user?.role || 'none'}</output>
      <output data-testid="token">{token || 'none'}</output>
      <output data-testid="admin">{String(isAdmin())}</output>
      <button onClick={() => login('support@example.com', 'test-only-password')}>login</button>
      <button onClick={logout}>logout</button>
    </div>
  );
}

beforeEach(() => {
  axios.get.mockReset();
  axios.post.mockReset();
  delete axios.defaults.headers.common.Authorization;
});

test('logs in an authorized staff role, persists token and logs out', async () => {
  axios.get.mockResolvedValue({ data: { id: 7, role: 'Support' } });
  axios.post.mockResolvedValue({
    data: { token: 'test-only-token', user: { id: 7, role: 'Support' } },
  });
  const user = userEvent.setup();
  render(<AuthProvider><AuthHarness /></AuthProvider>);
  await waitFor(() => expect(screen.getByTestId('loading')).toHaveTextContent('false'));

  await user.click(screen.getByRole('button', { name: 'login' }));
  await waitFor(() => expect(screen.getByTestId('role')).toHaveTextContent('Support'));
  await waitFor(() => expect(screen.getByTestId('admin')).toHaveTextContent('true'));
  expect(window.localStorage.getItem('token')).toBe('test-only-token');
  expect(axios.defaults.headers.common.Authorization).toBe('Bearer test-only-token');

  await user.click(screen.getByRole('button', { name: 'logout' }));
  await waitFor(() => expect(screen.getByTestId('token')).toHaveTextContent('none'));
  expect(window.localStorage.getItem('token')).toBeNull();
  expect(axios.defaults.headers.common.Authorization).toBeUndefined();
});

test('fails closed and removes a stale token when session validation fails', async () => {
  window.localStorage.setItem('token', 'test-only-stale-token');
  axios.get.mockRejectedValue(new Error('expired'));
  const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});

  render(<AuthProvider><AuthHarness /></AuthProvider>);

  await waitFor(() => expect(screen.getByTestId('loading')).toHaveTextContent('false'));
  await waitFor(() => expect(window.localStorage.getItem('token')).toBeNull());
  expect(screen.getByTestId('role')).toHaveTextContent('none');
  expect(screen.getByTestId('admin')).toHaveTextContent('false');
  consoleError.mockRestore();
});
