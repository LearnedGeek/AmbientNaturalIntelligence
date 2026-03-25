window.aniGraph = {
    instance: null,
    colorMode: 'type',

    init: function (graphData, colorMode) {
        this.colorMode = colorMode || 'type';
        const container = document.getElementById('graph-container');
        if (!container) return;
        if (typeof ForceGraph3D === 'undefined') {
            console.error('ForceGraph3D not loaded');
            return;
        }

        // Clean up previous instance
        if (this.instance) {
            container.innerHTML = '';
        }

        const typeColors = {
            'Semantic': '#2196f3',
            'Episodic': '#4caf50',
            'Perception': '#ff9800',
            'InnerThought': '#9c27b0',
            'Reflection': '#f44336',
        };

        const relColors = {
            'relates_to': 'rgba(255,255,255,0.08)',
            'caused_by': 'rgba(255,235,59,0.15)',
            'follows_up': 'rgba(0,188,212,0.15)',
            'contradicts': 'rgba(244,67,54,0.15)',
        };

        const now = Date.now();
        const dayMs = 86400000;
        const weekMs = dayMs * 7;

        const self = this;

        this.instance = ForceGraph3D()(container)
            .graphData(graphData)
            .nodeLabel(n => n.content)
            .nodeVal(n => 1 + (n.importance || 0.3) * 4)
            .nodeColor(n => {
                if (self.colorMode === 'type') {
                    return typeColors[n.type] || '#607d8b';
                } else if (self.colorMode === 'importance') {
                    const imp = n.importance || 0.3;
                    return imp >= 0.8 ? '#f44336' : imp >= 0.5 ? '#ff9800' : '#607d8b';
                } else {
                    const age = now - new Date(n.createdAt).getTime();
                    return age < dayMs ? '#4caf50' : age < weekMs ? '#ff9800' : '#607d8b';
                }
            })
            .nodeOpacity(0.9)
            .linkColor(l => relColors[l.relationship] || 'rgba(255,255,255,0.06)')
            .linkOpacity(0.4)
            .linkWidth(0.3)
            .backgroundColor('#0a0a1a')
            .width(container.clientWidth)
            .height(700)
            .onNodeClick(function (node) {
                const detail = document.getElementById('node-detail');
                const detailType = document.getElementById('detail-type');
                const detailContent = document.getElementById('detail-content');
                const detailMeta = document.getElementById('detail-meta');

                if (detail && detailType && detailContent && detailMeta) {
                    detail.style.display = 'block';
                    detailType.textContent = node.type + (node.isAnchored ? ' (anchored)' : '');
                    detailContent.textContent = node.content;
                    detailMeta.textContent = 'Importance: ' + (node.importance || 0).toFixed(2) +
                        ' | Created: ' + new Date(node.createdAt).toLocaleString();
                }

                // Focus camera on clicked node
                const distance = 80;
                const distRatio = 1 + distance / Math.hypot(node.x, node.y, node.z);
                self.instance.cameraPosition(
                    { x: node.x * distRatio, y: node.y * distRatio, z: node.z * distRatio },
                    node,
                    1500
                );
            });
    },

    updateColorMode: function (mode) {
        this.colorMode = mode;
        if (this.instance) {
            // Force re-render with new colors
            this.instance.nodeColor(this.instance.nodeColor());
        }
    }
};
