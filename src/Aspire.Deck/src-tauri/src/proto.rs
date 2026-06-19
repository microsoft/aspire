// Generated gRPC/protobuf bindings.
//
// prost generates cross-package type references assuming the Rust module tree
// mirrors the protobuf package hierarchy (e.g. a type in
// `opentelemetry.proto.trace.v1` that references `opentelemetry.proto.common.v1`
// resolves via `super::super::common::v1::...`). The nesting below MUST match the
// package segments exactly, or the generated code will not compile.
#![allow(clippy::all)]
#![allow(rustdoc::all)]

pub mod aspire {
    pub mod v1 {
        tonic::include_proto!("aspire.v1");
    }
}

pub mod opentelemetry {
    pub mod proto {
        pub mod common {
            pub mod v1 {
                tonic::include_proto!("opentelemetry.proto.common.v1");
            }
        }
        pub mod resource {
            pub mod v1 {
                tonic::include_proto!("opentelemetry.proto.resource.v1");
            }
        }
        pub mod trace {
            pub mod v1 {
                tonic::include_proto!("opentelemetry.proto.trace.v1");
            }
        }
        pub mod metrics {
            pub mod v1 {
                tonic::include_proto!("opentelemetry.proto.metrics.v1");
            }
        }
        pub mod logs {
            pub mod v1 {
                tonic::include_proto!("opentelemetry.proto.logs.v1");
            }
        }
        pub mod collector {
            pub mod trace {
                pub mod v1 {
                    tonic::include_proto!("opentelemetry.proto.collector.trace.v1");
                }
            }
            pub mod metrics {
                pub mod v1 {
                    tonic::include_proto!("opentelemetry.proto.collector.metrics.v1");
                }
            }
            pub mod logs {
                pub mod v1 {
                    tonic::include_proto!("opentelemetry.proto.collector.logs.v1");
                }
            }
        }
    }
}
