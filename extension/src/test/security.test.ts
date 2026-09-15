import * as assert from 'assert';
import { X509Certificate } from 'crypto';
import forge from 'node-forge';
import { createSelfSignedCertAsync, generateCertificateSerialNumber, generateToken } from '../utils/security';

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

    test('generateCertificateSerialNumber emits a serial that OpenSSL can parse', () => {
        const serial = generateCertificateSerialNumber(Buffer.from([
            0x80, 0x00, 0x01, 0x45,
            0x67, 0x89, 0xab, 0xcd,
            0xef, 0x01, 0x23, 0x45,
            0x67, 0x89, 0xab, 0xcd,
        ]));

        const certPem = createCertificatePemWithSerialNumber(serial);

        assert.doesNotThrow(() => new X509Certificate(certPem));
    });

    test('generateCertificateSerialNumber normalizes the first byte to a non-zero positive value', () => {
        const serial = generateCertificateSerialNumber(Buffer.from([
            0x80, 0x01, 0x23, 0x45,
            0x67, 0x89, 0xab, 0xcd,
            0xef, 0x01, 0x23, 0x45,
            0x67, 0x89, 0xab, 0xcd,
        ]));

        assert.ok(!serial.startsWith('00'));
        assert.ok(Number.parseInt(serial.slice(0, 2), 16) < 0x80);
        assert.strictEqual(serial.length, 32);
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

function createCertificatePemWithSerialNumber(serialNumber: string): string {
    const pki = forge.pki;
    // The key strength is irrelevant to this test; keep it small so the DER serial regression stays fast.
    const keys = pki.rsa.generateKeyPair(512);
    const cert = pki.createCertificate();
    cert.publicKey = keys.publicKey;
    cert.serialNumber = serialNumber;
    cert.validity.notBefore = new Date();
    cert.validity.notAfter = new Date(Date.now() + 60_000);

    const attrs = [{ name: 'commonName', value: 'localhost' }];
    cert.setSubject(attrs);
    cert.setIssuer(attrs);
    cert.sign(keys.privateKey);

    return pki.certificateToPem(cert);
}
