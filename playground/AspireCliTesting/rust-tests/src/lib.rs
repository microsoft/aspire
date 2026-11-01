// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#[cfg(test)]
mod tests {
    use std::io::{Read, Write};
    use std::net::{TcpStream, ToSocketAddrs};
    use std::time::Duration;

    #[test]
    fn service_response() {
        assert_eq!(get_service("/"), "Aspire CLI testing");
    }

    #[test]
    fn service_health() {
        get_service("/health");
    }

    #[test]
    fn controlled_failure() {
        let fail = std::env::var("RUST_PLAYGROUND_FAIL").unwrap_or_default();
        assert!(
            !fail.eq_ignore_ascii_case("true"),
            "Opt-in Rust playground failure."
        );
    }

    #[test]
    #[ignore = "Demonstrates a skipped Rust result in the common summary."]
    fn skipped_example() {
        panic!("This ignored test must not execute.");
    }

    fn get_service(path: &str) -> String {
        let endpoint = std::env::var("RUST_SERVICE_URL")
            .expect("RUST_SERVICE_URL must contain the shared endpoint");
        let authority = endpoint
            .trim_end_matches('/')
            .strip_prefix("http://")
            .expect("The shared service must use plain HTTP");
        let addresses = authority
            .to_socket_addrs()
            .expect("Could not resolve the shared service endpoint");
        let mut connection = None;
        let mut last_error = None;
        for address in addresses {
            match TcpStream::connect_timeout(&address, Duration::from_secs(10)) {
                Ok(stream) => {
                    connection = Some(stream);
                    break;
                }
                Err(error) => last_error = Some(error),
            }
        }
        let mut stream = connection
            .unwrap_or_else(|| panic!("Could not connect to {authority}: {last_error:?}"));
        stream
            .set_read_timeout(Some(Duration::from_secs(10)))
            .expect("Could not set the HTTP read timeout");
        stream
            .set_write_timeout(Some(Duration::from_secs(10)))
            .expect("Could not set the HTTP write timeout");
        // HTTP/1.0 with Connection: close avoids chunked decoding in this dependency-free probe.
        // Response shape: "HTTP/1.1 200 OK\r\n<header lines>\r\n\r\n<body>".
        write!(
            stream,
            "GET {path} HTTP/1.0\r\nHost: {authority}\r\nConnection: close\r\n\r\n"
        )
        .expect("Could not write the HTTP request");
        let mut response = String::new();
        stream
            .read_to_string(&mut response)
            .expect("Could not read the HTTP response");
        let (headers, body) = response
            .split_once("\r\n\r\n")
            .expect("The HTTP response has no header/body delimiter");
        assert_eq!(
            headers.lines().next().unwrap().split_whitespace().nth(1),
            Some("200"),
            "GET {path} failed: {response}"
        );
        body.to_owned()
    }
}
