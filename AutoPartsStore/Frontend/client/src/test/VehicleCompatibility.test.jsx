import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { beforeEach, expect, test, vi } from 'vitest';
import VehicleCompatibility from '../components/VehicleCompatibility';
import { fitmentAPI } from '../services/api';

vi.mock('../services/api', () => ({
  fitmentAPI: {
    getMakes: vi.fn(),
    getModels: vi.fn(),
    getGenerations: vi.fn(),
    getEngines: vi.fn(),
    getConfigurations: vi.fn(),
    getForProduct: vi.fn(),
    check: vi.fn(),
  },
}));

beforeEach(() => {
  Object.values(fitmentAPI).forEach(mock => mock.mockReset());
  fitmentAPI.getMakes.mockResolvedValue({ data: [{ id: 1, name: 'Test Make' }] });
  fitmentAPI.getForProduct.mockResolvedValue({ data: { items: [] } });
  fitmentAPI.getModels.mockResolvedValue({ data: [{ id: 2, name: 'Test Model' }] });
  fitmentAPI.getGenerations.mockResolvedValue({ data: [{ id: 3, name: 'Test Generation' }] });
  fitmentAPI.getEngines.mockResolvedValue({ data: [{ id: 4, name: 'Test Engine' }] });
  fitmentAPI.getConfigurations.mockResolvedValue({ data: [{ id: 5, name: 'Test Vehicle' }] });
  fitmentAPI.check.mockResolvedValue({
    data: {
      match: 'Exact',
      confidence: 0.98,
      confidenceBand: 'VeryHigh',
      isVerified: true,
      message: 'Verified exact fitment',
      sourceName: 'Test Catalog',
    },
  });
});

test('walks the dependent vehicle selector and stores verified compatibility', async () => {
  const user = userEvent.setup();
  render(<MemoryRouter><VehicleCompatibility productId={101} /></MemoryRouter>);

  const selects = screen.getAllByRole('combobox');
  await screen.findByRole('option', { name: 'Test Make' });
  await user.selectOptions(selects[0], '1');
  await waitFor(() => expect(fitmentAPI.getModels).toHaveBeenCalledWith('1'));
  await screen.findByRole('option', { name: 'Test Model' });
  await user.selectOptions(selects[1], '2');
  await waitFor(() => expect(fitmentAPI.getGenerations).toHaveBeenCalledWith('2'));
  await screen.findByRole('option', { name: 'Test Generation' });
  await user.selectOptions(selects[2], '3');
  await waitFor(() => expect(fitmentAPI.getEngines).toHaveBeenCalledWith('3'));
  await screen.findByRole('option', { name: 'Test Engine' });
  await user.selectOptions(selects[3], '4');
  await waitFor(() => expect(fitmentAPI.getConfigurations).toHaveBeenCalledWith('4'));
  await screen.findByRole('option', { name: 'Test Vehicle' });
  await user.selectOptions(selects[4], '5');

  const action = screen.getAllByRole('button').at(-1);
  expect(action).not.toBeDisabled();
  await user.click(action);

  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Verified exact fitment'));
  expect(fitmentAPI.check).toHaveBeenCalledWith(101, '5');
  expect(JSON.parse(window.localStorage.getItem('parca-muhendisi:selected-vehicle'))).toEqual({
    vehicleId: 5,
    name: 'Test Vehicle',
  });
});

test('shows a safe error instead of an unverified result when catalog loading fails', async () => {
  fitmentAPI.getMakes.mockRejectedValue(new Error('catalog unavailable'));
  render(<MemoryRouter><VehicleCompatibility productId={202} /></MemoryRouter>);

  expect(await screen.findByRole('alert')).toBeInTheDocument();
  expect(screen.queryByRole('status')).not.toBeInTheDocument();
});
