import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, test } from 'vitest';
import { CartProvider, useCart } from '../context/CartContext';

const testProduct = { id: 42, name: 'Test Part', price: 125.5 };

function CartHarness() {
  const {
    cart,
    addToCart,
    updateQuantity,
    removeFromCart,
    clearCart,
    getCartCount,
    getCartTotal,
  } = useCart();

  return (
    <div>
      <output data-testid="count">{getCartCount()}</output>
      <output data-testid="total">{getCartTotal()}</output>
      <output data-testid="items">{cart.length}</output>
      <button onClick={() => addToCart(testProduct)}>add</button>
      <button onClick={() => updateQuantity(testProduct.id, 3)}>set-three</button>
      <button onClick={() => updateQuantity(testProduct.id, 0)}>set-zero</button>
      <button onClick={() => removeFromCart(testProduct.id)}>remove</button>
      <button onClick={clearCart}>clear</button>
    </div>
  );
}

const renderCart = () => render(<CartProvider><CartHarness /></CartProvider>);

test('adds, merges, updates, removes and persists cart state', async () => {
  const user = userEvent.setup();
  renderCart();

  await user.click(screen.getByRole('button', { name: 'add' }));
  await user.click(screen.getByRole('button', { name: 'add' }));
  expect(screen.getByTestId('count')).toHaveTextContent('2');
  expect(screen.getByTestId('items')).toHaveTextContent('1');

  await user.click(screen.getByRole('button', { name: 'set-three' }));
  expect(screen.getByTestId('count')).toHaveTextContent('3');
  expect(screen.getByTestId('total')).toHaveTextContent('376.5');
  expect(JSON.parse(window.localStorage.getItem('cart'))).toEqual([
    { ...testProduct, quantity: 3 },
  ]);

  await user.click(screen.getByRole('button', { name: 'set-zero' }));
  expect(screen.getByTestId('items')).toHaveTextContent('0');
});

test('restores saved cart and supports explicit remove and clear', async () => {
  window.localStorage.setItem('cart', JSON.stringify([{ ...testProduct, quantity: 2 }]));
  const user = userEvent.setup();
  renderCart();
  expect(screen.getByTestId('count')).toHaveTextContent('2');

  await user.click(screen.getByRole('button', { name: 'remove' }));
  expect(screen.getByTestId('items')).toHaveTextContent('0');
  await user.click(screen.getByRole('button', { name: 'add' }));
  await user.click(screen.getByRole('button', { name: 'clear' }));
  expect(screen.getByTestId('items')).toHaveTextContent('0');
});
