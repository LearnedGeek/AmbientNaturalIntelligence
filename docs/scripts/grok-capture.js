(function() {
    console.clear();
    console.log('GROK SCRAPER - ROUND 2');
    
    var scrollAttempts = 0;
    var stableCount = 0;
    var previousHeight = 0;
    var startTime = Date.now();
    
    var container = document.querySelector('.overflow-y-auto.scrollbar-gutter-stable');
    
    if (!container) {
        console.error('Container not found');
        return;
    }
    
    console.log('Started at: ' + new Date().toLocaleTimeString());
    
    function saveNow(isFinal) {
        var messages = document.querySelectorAll('.message-bubble');
        var elapsed = Math.floor((Date.now() - startTime) / 1000);
        var mins = Math.floor(elapsed / 60);
        var secs = elapsed % 60;
        
        var content = 'GROK CHAT EXPORT ' + (isFinal ? '(FINAL)' : '(CHECKPOINT)') + '\n';
        content += 'Export Date: ' + new Date().toLocaleString() + '\n';
        content += 'Messages: ' + messages.length + '\n';
        content += 'Time: ' + mins + 'm ' + secs + 's\n\n';
        content += '='.repeat(100) + '\n\n';
        
        for (var i = 0; i < messages.length; i++) {
            var msg = messages[i];
            var isUser = msg.classList.contains('bg-surface-l1');
            var role = isUser ? 'YOU' : 'GROK';
            var text = msg.innerText || msg.textContent || '';
            
            content += '[Message ' + (i + 1) + '] ' + role + ':\n';
            content += '-'.repeat(100) + '\n';
            content += text.trim() + '\n\n';
            content += '='.repeat(100) + '\n\n';
        }
        
        var blob = new Blob([content], { type: 'text/plain' });
        var url = URL.createObjectURL(blob);
        var link = document.createElement('a');
        link.href = url;
        link.download = isFinal ? 
            'grok-FINAL-' + Date.now() + '.txt' : 
            'grok-checkpoint-' + messages.length + 'msgs-' + Date.now() + '.txt';
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        URL.revokeObjectURL(url);
        
        console.log((isFinal ? 'FINAL' : 'CHECKPOINT') + ' saved: ' + messages.length + ' messages');
    }
    
    function doScroll() {
        scrollAttempts++;
        container.scrollTop = 0;
        
        setTimeout(function() {
            var currentHeight = container.scrollHeight;
            var currentMsgs = document.querySelectorAll('.message-bubble').length;
            
            // Save checkpoint every 50 attempts
            if (scrollAttempts % 50 === 0) {
                saveNow(false);
            }
            
            // Progress update every 10
            if (scrollAttempts % 10 === 0) {
                console.log('Progress: ' + scrollAttempts + ' attempts, ' + currentMsgs + ' messages');
            }
            
            if (currentHeight === previousHeight) {
                stableCount++;
                console.log('Stable ' + stableCount + '/5');  // Changed from 3 to 5
                
                if (stableCount >= 5) {  // More thorough check
                    console.log('COMPLETE!');
                    saveNow(true);
                    return;
                }
            } else {
                stableCount = 0;
                console.log('New content: ' + currentMsgs + ' messages');
                previousHeight = currentHeight;
            }
            
            if (scrollAttempts < 1000) {
                doScroll();
            }
        }, 3000);  // Increased delay from 2500 to 3000ms
    }
    
    doScroll();
})();