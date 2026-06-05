import * as assert from 'assert';
import { X509Certificate } from 'crypto';
import { createSelfSignedCertAsync, generateToken } from '../utils/security';

suite('Security utilities', () => {
    test('createSelfSignedCertAsync generates a valid certificate', async () => {
        const result = await createSelfSignedCertAsync('test-host');

        // Verify the result contains all expected properties
        assert.ok(result.key, 'Should have a private key');
        assert.ok(result.cert, 'Should have a certificate');
        assert.ok(result.certBase64, 'Should have a base64-encoded certificate');

        // Verify the PEM format
        assert.ok(result.key.includes('-----BEGIN RSA PRIVATE KEY-----'), 'Key should be in PEM format');
        assert.ok(result.cert.includes('-----BEGIN CERTIFICATE-----'), 'Cert should be in PEM format');

        // Verify the certificate can be parsed by Node.js crypto
        const x509 = new X509Certificate(result.cert);
        assert.ok(x509.subject.includes('test-host'), 'Subject should contain the common name');
    });

    test('createSelfSignedCertAsync produces certificate that can be parsed multiple times', async () => {
        // Run multiple times to catch intermittent issues with serial number generation
        for (let i = 0; i < 10; i++) {
            const result = await createSelfSignedCertAsync();

            // The key validation is that X509Certificate doesn't throw
            const x509 = new X509Certificate(result.cert);
            assert.ok(x509.serialNumber, `Iteration ${i}: Should have a serial number`);

            // Verify serial number is a valid hex string (no leading zeros issues)
            const serialHex = x509.serialNumber.replace(/:/g, '');
            assert.ok(/^[0-9a-fA-F]+$/.test(serialHex), `Iteration ${i}: Serial number should be valid hex`);
        }
    });

    test('generateToken returns a base64 string', () => {
        const token = generateToken();
        assert.ok(token, 'Token should not be empty');
        // Base64 string should be decodable
        const decoded = Buffer.from(token, 'base64');
        assert.strictEqual(decoded.length, 32, 'Token should be 32 bytes when decoded');
    });

    test('generateToken produces unique values', () => {
        const tokens = new Set<string>();
        for (let i = 0; i < 100; i++) {
            tokens.add(generateToken());
        }
        assert.strictEqual(tokens.size, 100, 'All tokens should be unique');
    });
});
