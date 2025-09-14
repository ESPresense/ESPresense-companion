import { test, expect, type Page } from '@playwright/test';

// Helper to create a test page with modal functionality
async function setupModalTest(page: Page) {
  // Create a simple test page with modal
  await page.setContent(`
    <!DOCTYPE html>
    <html>
    <head>
      <title>Modal Test</title>
      <style>
        .modal-backdrop {
          position: fixed;
          inset: 0;
          z-index: 50;
          display: flex;
          align-items: center;
          justify-content: center;
          background-color: rgba(0, 0, 0, 0.5);
        }
        .modal-content {
          background: white;
          padding: 2rem;
          border-radius: 8px;
          max-width: 500px;
          width: 90%;
        }
        .hidden { display: none; }
        button {
          margin: 0.5rem;
          padding: 0.5rem 1rem;
          border: 1px solid #ccc;
          cursor: pointer;
        }
      </style>
    </head>
    <body>
      <button id="open-modal">Open Modal</button>
      
      <div id="modal" class="modal-backdrop hidden" role="dialog" aria-modal="true">
        <div class="modal-content" onclick="event.stopPropagation()">
          <h2>Test Modal</h2>
          <p>This is a test modal for keyboard and click behavior.</p>
          <input id="modal-input" type="text" placeholder="Test input">
          <textarea id="modal-textarea" placeholder="Test textarea"></textarea>
          <div id="modal-contenteditable" contenteditable="true">Editable content</div>
          <button id="modal-button">Modal Button</button>
          <div>
            <button id="confirm-btn">Confirm</button>
            <button id="cancel-btn">Cancel</button>
          </div>
        </div>
      </div>

      <script>
        let modalStack = [];
        
        function isInsideInteractive(element) {
          if (!element) return false;
          
          const interactiveTags = ['INPUT', 'TEXTAREA', 'BUTTON', 'SELECT', 'A'];
          if (interactiveTags.includes(element.tagName)) return true;
          
          if (element.isContentEditable) return true;
          
          if (element.hasAttribute('tabindex') && element.getAttribute('tabindex') !== '-1') return true;
          if (element.getAttribute('role') === 'button' || element.getAttribute('role') === 'link') return true;
          
          return isInsideInteractive(element.parentElement);
        }
        
        let confirmHandler = null;
        let cancelHandler = null;
        
        document.getElementById('open-modal').onclick = () => {
          document.getElementById('modal').classList.remove('hidden');
          modalStack.push({
            onConfirm: () => { 
              console.log('confirmed');
              document.getElementById('modal').classList.add('hidden');
              modalStack.pop();
            },
            onCancel: () => { 
              console.log('cancelled'); 
              document.getElementById('modal').classList.add('hidden');
              modalStack.pop();
            }
          });
        };
        
        document.getElementById('confirm-btn').onclick = () => {
          const modal = modalStack[modalStack.length - 1];
          if (modal?.onConfirm) modal.onConfirm();
        };
        
        document.getElementById('cancel-btn').onclick = () => {
          const modal = modalStack[modalStack.length - 1];
          if (modal?.onCancel) modal.onCancel();
        };
        
        document.getElementById('modal').onclick = (e) => {
          if (e.target === e.currentTarget) {
            const modal = modalStack[modalStack.length - 1];
            if (modal?.onCancel) {
              modal.onCancel();
            } else if (modal?.onConfirm) {
              modal.onConfirm();
            } else {
              document.getElementById('modal').classList.add('hidden');
              modalStack.pop();
            }
          }
        };
        
        document.addEventListener('keydown', (event) => {
          if (modalStack.length === 0) return;
          
          const modal = modalStack[modalStack.length - 1];
          
          if (event.key === 'Escape') {
            event.preventDefault();
            event.stopPropagation();
            if (modal.onCancel) {
              modal.onCancel();
            } else if (modal.onConfirm) {
              modal.onConfirm();
            } else {
              document.getElementById('modal').classList.add('hidden');
              modalStack.pop();
            }
          } else if (event.key === 'Enter') {
            const target = event.target;
            if (isInsideInteractive(target)) {
              return;
            }
            event.preventDefault();
            if (modal.onConfirm) {
              modal.onConfirm();
            }
          }
        });
      </script>
    </body>
    </html>
  `);
}

test.describe('Modal Component', () => {
  test('should open modal when button is clicked', async ({ page }) => {
    await setupModalTest(page);
    
    // Modal should be hidden initially
    await expect(page.locator('#modal')).toHaveClass(/hidden/);
    
    // Click to open modal
    await page.click('#open-modal');
    
    // Modal should be visible
    await expect(page.locator('#modal')).not.toHaveClass(/hidden/);
    await expect(page.locator('[role="dialog"]')).toBeVisible();
  });
  
  test('should close modal when Escape key is pressed', async ({ page }) => {
    await setupModalTest(page);
    
    // Open modal
    await page.click('#open-modal');
    await expect(page.locator('#modal')).toBeVisible();
    
    // Press Escape
    await page.keyboard.press('Escape');
    
    // Modal should be closed
    await expect(page.locator('#modal')).toHaveClass(/hidden/);
  });
  
  test('should close modal when clicking backdrop', async ({ page }) => {
    await setupModalTest(page);
    
    // Open modal
    await page.click('#open-modal');
    await expect(page.locator('#modal')).toBeVisible();
    
    // Click on backdrop area (coordinates that should be outside modal content but inside modal)
    const modalBounds = await page.locator('#modal').boundingBox();
    const contentBounds = await page.locator('.modal-content').boundingBox();
    
    if (modalBounds && contentBounds) {
      // Click in the top-left area of the modal but outside the content
      await page.mouse.click(modalBounds.x + 10, modalBounds.y + 10);
    }
    
    // Modal should be closed
    await expect(page.locator('#modal')).toHaveClass(/hidden/);
  });
  
  test('should NOT close modal when clicking inside modal content', async ({ page }) => {
    await setupModalTest(page);
    
    // Open modal
    await page.click('#open-modal');
    await expect(page.locator('#modal')).toBeVisible();
    
    // Click inside modal content
    await page.click('.modal-content');
    
    // Modal should still be visible
    await expect(page.locator('#modal')).not.toHaveClass(/hidden/);
  });
  
  test('should NOT trigger modal action when Enter is pressed in input field', async ({ page }) => {
    await setupModalTest(page);
    
    const messages: string[] = [];
    page.on('console', msg => messages.push(msg.text()));
    
    // Open modal
    await page.click('#open-modal');
    await expect(page.locator('#modal')).toBeVisible();
    
    // Focus on input field
    await page.click('#modal-input');
    
    // Type in the input
    await page.keyboard.type('test');
    
    // Press Enter while in input
    await page.keyboard.press('Enter');
    
    // Modal should still be visible and no action triggered
    await expect(page.locator('#modal')).not.toHaveClass(/hidden/);
    expect(messages).not.toContain('confirmed');
    expect(messages).not.toContain('cancelled');
  });
  
  test('should NOT trigger modal action when Enter is pressed in textarea', async ({ page }) => {
    await setupModalTest(page);
    
    const messages: string[] = [];
    page.on('console', msg => messages.push(msg.text()));
    
    // Open modal
    await page.click('#open-modal');
    await expect(page.locator('#modal')).toBeVisible();
    
    // Focus on textarea
    await page.click('#modal-textarea');
    
    // Press Enter while in textarea
    await page.keyboard.press('Enter');
    
    // Modal should still be visible
    await expect(page.locator('#modal')).not.toHaveClass(/hidden/);
    expect(messages).not.toContain('confirmed');
  });
  
  test('should NOT trigger modal action when Enter is pressed in contentEditable', async ({ page }) => {
    await setupModalTest(page);
    
    const messages: string[] = [];
    page.on('console', msg => messages.push(msg.text()));
    
    // Open modal
    await page.click('#open-modal');
    await expect(page.locator('#modal')).toBeVisible();
    
    // Focus on contentEditable
    await page.click('#modal-contenteditable');
    
    // Press Enter while in contentEditable
    await page.keyboard.press('Enter');
    
    // Modal should still be visible
    await expect(page.locator('#modal')).not.toHaveClass(/hidden/);
    expect(messages).not.toContain('confirmed');
  });
  
  test('should work with confirm and cancel buttons', async ({ page }) => {
    await setupModalTest(page);
    
    const messages: string[] = [];
    page.on('console', msg => messages.push(msg.text()));
    
    // Test confirm button
    await page.click('#open-modal');
    await page.click('#confirm-btn');
    expect(messages).toContain('confirmed');
    
    // Test cancel button  
    await page.click('#open-modal');
    await page.click('#cancel-btn');
    expect(messages).toContain('cancelled');
  });
  
  test('should handle keyboard navigation properly', async ({ page }) => {
    await setupModalTest(page);
    
    // Open modal
    await page.click('#open-modal');
    
    // Tab through interactive elements
    await page.keyboard.press('Tab');
    let focused = await page.evaluate(() => document.activeElement?.id);
    expect(['modal-input', 'modal-textarea', 'modal-contenteditable', 'modal-button', 'confirm-btn', 'cancel-btn']).toContain(focused);
    
    // Continue tabbing
    await page.keyboard.press('Tab');
    let newFocused = await page.evaluate(() => document.activeElement?.id);
    expect(newFocused).not.toBe(focused);
  });
});