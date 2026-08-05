import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, test, vi } from 'vitest';
import Pagination from '../components/Pagination';

test('hides for one page and emits only valid requested pages', async () => {
  const { rerender } = render(
    <Pagination currentPage={1} totalPages={1} onPageChange={() => {}} />
  );
  expect(screen.queryByRole('button')).not.toBeInTheDocument();

  const onPageChange = vi.fn();
  rerender(<Pagination currentPage={5} totalPages={10} onPageChange={onPageChange} />);
  const user = userEvent.setup();
  const ellipses = screen.getAllByRole('button', { name: '...' });
  expect(ellipses).toHaveLength(2);
  expect(ellipses.every(button => button.disabled)).toBe(true);

  await user.click(screen.getByRole('button', { name: '6' }));
  expect(onPageChange).toHaveBeenCalledWith(6);
});

test('disables navigation at boundaries', () => {
  const { rerender } = render(
    <Pagination currentPage={1} totalPages={3} onPageChange={() => {}} />
  );
  const buttons = screen.getAllByRole('button');
  expect(buttons[0]).toBeDisabled();
  expect(buttons.at(-1)).not.toBeDisabled();

  rerender(<Pagination currentPage={3} totalPages={3} onPageChange={() => {}} />);
  const endButtons = screen.getAllByRole('button');
  expect(endButtons.at(-1)).toBeDisabled();
});

test('keeps useful page windows near the beginning and end of a long result set', () => {
  const { rerender } = render(
    <Pagination currentPage={2} totalPages={10} onPageChange={() => {}} />
  );
  expect(screen.getByRole('button', { name: '4' })).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: '7' })).not.toBeInTheDocument();

  rerender(<Pagination currentPage={9} totalPages={10} onPageChange={() => {}} />);
  expect(screen.getByRole('button', { name: '7' })).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: '4' })).not.toBeInTheDocument();
});
