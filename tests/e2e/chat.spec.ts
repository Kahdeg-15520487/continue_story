import { test, expect, createAndProcessBook } from './helpers';

const bookContent = Buffer.from(`# My Book

This is a test book about a character named Alice who lives in Wonderland.
She follows a white rabbit down a hole and has many adventures.

## Chapter 2

Alice meets the Cheshire Cat and attends a tea party with the Mad Hatter.
`);

test.describe('Chat Interaction', () => {
  let slug: string;

  test.beforeAll(async () => {
    test.setTimeout(300_000);
    slug = await createAndProcessBook('Chat Test Book', bookContent);
  });

  test('send message and receive response', async ({ page }) => {
    await page.goto(`/books/${slug}`);

    // Open chat panel
    await page.getByRole('button', { name: /chat/i }).click();

    // Type message
    const chatInput = page.locator('.chat-input, textarea');
    await chatInput.fill('Who is the main character?');
    await chatInput.press('Enter');

    // User message should appear
    await expect(page.locator('.message, .chat-message').first()).toBeVisible({ timeout: 5000 });

    // Assistant response should stream in
    await expect(page.locator('.message, .chat-message').nth(1)).toBeVisible({ timeout: 30_000 });
  });

  test('chat persists after page reload', async ({ page }) => {
    await page.goto(`/books/${slug}`);
    await page.getByRole('button', { name: /chat/i }).click();

    // Send a message
    const chatInput = page.locator('.chat-input, textarea');
    await chatInput.fill('What happens in chapter 2?');
    await chatInput.press('Enter');

    // Wait for response
    await expect(page.locator('.message, .chat-message').nth(1)).toBeVisible({ timeout: 30_000 });

    // Reload page
    await page.reload();

    // Reopen chat
    await page.getByRole('button', { name: /chat/i }).click();

    // Previous messages should still be visible
    const messages = page.locator('.message, .chat-message');
    await expect(messages.first()).toBeVisible({ timeout: 5000 });
  });
});
