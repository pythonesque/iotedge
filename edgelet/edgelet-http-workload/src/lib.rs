// Copyright (c) Microsoft. All rights reserved.

#![deny(warnings)]

extern crate base64;
extern crate chrono;
extern crate edgelet_core;
extern crate edgelet_hsm;
#[macro_use]
extern crate edgelet_http;
#[cfg(test)]
extern crate edgelet_test_utils;
#[macro_use]
extern crate edgelet_utils;
extern crate failure;
#[macro_use]
extern crate failure_derive;
extern crate futures;
extern crate http;
extern crate hyper;
#[macro_use]
extern crate log;
extern crate serde;
extern crate serde_json;
extern crate workload;

use http::Response;
use hyper::Body;

mod error;
mod server;

pub use server::WorkloadService;

pub trait IntoResponse {
    fn into_response(self) -> Response<Body>;
}

impl IntoResponse for Response<Body> {
    fn into_response(self) -> Response<Body> {
        self
    }
}
