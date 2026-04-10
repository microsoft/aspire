from flask import Flask, jsonify
import logging
import os

def create_app():
    """Application factory for Flask app."""
    app = Flask(__name__)

    # Configure logging
    logging.basicConfig(level=logging.INFO)
    logger = logging.getLogger(__name__)

    @app.route('/')
    def hello():
        logger.info("Hello endpoint called")
        return jsonify({
            'message': 'Hello from Flask!',
            'status': 'running'
        })

    @app.route('/health')
    def health():
        return jsonify({'status': 'healthy'})

    @app.route('/api/data')
    def get_data():
        logger.info("Data endpoint called")
        return jsonify({
            'items': [
                {'id': 1, 'name': 'Item 1'},
                {'id': 2, 'name': 'Item 2'},
                {'id': 3, 'name': 'Item 3'}
            ]
        })

    return app

# For running directly with python app.py
if __name__ == '__main__':
    app = create_app()
    debug_mode = os.getenv('FLASK_DEBUG', '').lower() in ('1', 'true', 'yes')
    app.run(debug=debug_mode, host='0.0.0.0', port=5000)
